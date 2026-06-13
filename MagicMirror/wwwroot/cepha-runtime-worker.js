// 🧬 Cepha Runtime Worker — runs .NET WASM off the main thread
// All MVC processing, EF Core, and SQLite operations happen here.
// The main thread only handles DOM updates and user events.

// ─── Global error handlers ──────────────────────────────────
self.onerror = (msg, src, line, col, err) =>
    console.error('🧬 Worker error:', msg, err);
self.onunhandledrejection = (e) =>
    console.error('🧬 Worker unhandled rejection:', e.reason);

let _storage = {};
let _currentPath = '/';
let _exports = null;
let _msgId = 0;
const _pending = new Map();

// ─── Wait for init data from main thread ─────────────────────

self.postMessage({ type: 'created' });

const initData = await new Promise(resolve => {
    self.addEventListener('message', function handler(e) {
        if (e.data.type === 'init') {
            self.removeEventListener('message', handler);
            resolve(e.data);
        }
    });
});

_storage = initData.storage || {};
_currentPath = initData.path || '/';
const _fingerprint = initData.fingerprint || 'unknown';

// ─── Dynamic import of dotnet.js (resolves fingerprinted URL from main thread) ──
const dotnetUrl = initData.dotnetUrl || './_framework/dotnet.js';
const { dotnet } = await import(dotnetUrl);

// ─── Boot .NET WASM Runtime ─────────────────────────────────

const { setModuleImports, getAssemblyExports, getConfig, runMain } = await dotnet
    .withApplicationArguments("start")
    .create();

console.log('%c🧬 Cepha: .NET runtime created', 'color: #667eea');

// Register JS functions — DOM ops post back to main thread
setModuleImports('main.js', {
    dom: {
        setInnerHTML: (selector, html) =>
            self.postMessage({ type: 'dom', op: 'setInnerHTML', selector, value: html }),
        setInnerText: (selector, text) =>
            self.postMessage({ type: 'dom', op: 'setInnerText', selector, value: text }),
        getInnerText: () => '',
        setAttribute: (selector, attr, value) =>
            self.postMessage({ type: 'dom', op: 'setAttribute', selector, attr, value }),
        addClass: (selector, cls) =>
            self.postMessage({ type: 'dom', op: 'addClass', selector, value: cls }),
        removeClass: (selector, cls) =>
            self.postMessage({ type: 'dom', op: 'removeClass', selector, value: cls }),
        show: (selector) =>
            self.postMessage({ type: 'dom', op: 'show', selector }),
        hide: (selector) =>
            self.postMessage({ type: 'dom', op: 'hide', selector }),
        // Streaming: plasma source pushes frames in chunks
        streamStart: (selector) =>
            self.postMessage({ type: 'dom', op: 'streamStart', selector }),
        streamAppend: (selector, html) =>
            self.postMessage({ type: 'dom', op: 'streamAppend', selector, value: html }),
        streamEnd: (selector) =>
            self.postMessage({ type: 'dom', op: 'streamEnd', selector })
    },
    storage: {
        getItem: (key) => _storage[key] ?? null,
        setItem: (key, value) => {
            _storage[key] = value;
            self.postMessage({ type: 'storage', op: 'set', key, value });
        },
        removeItem: (key) => {
            delete _storage[key];
            self.postMessage({ type: 'storage', op: 'remove', key });
        }
    },
    navigation: {
        getPath: () => _currentPath,
        getFingerprint: () => _fingerprint,
        pushState: (path) => {
            _currentPath = path;
            self.postMessage({ type: 'pushState', path });
        },
        navigateTo: async (path) => {
            _currentPath = path;
            self.postMessage({ type: 'pushState', path });
            if (_exports) await _exports.Cepha.JsExports.Navigate(path);
        }
    },
    fileOps: {
        downloadFile: (name, b64, mime) =>
            self.postMessage({ type: 'download', name, b64, mime })
    },
    signalr: {
        dispatchHubEvent: (hubName, method, connectionId, argsJson) =>
            self.postMessage({ type: 'signalr', hubName, method, connectionId, argsJson })
    },
    cephakit: {
        start: (port) => self.postMessage({ type: 'cephakit', port })
    },
    auth: {
        broadcast: (action) => self.postMessage({ type: 'auth-changed', action })
    },
    cephaDb: {
        restore: () => new Promise(resolve => {
            const id = ++_msgId;
            _pending.set(id, resolve);
            self.postMessage({ type: 'cephaDb', op: 'restore', id });
        }),
        persist: (base64) => new Promise(resolve => {
            const id = ++_msgId;
            _pending.set(id, resolve);
            self.postMessage({ type: 'cephaDb', op: 'persist', id, base64 });
        })
    },
    opfs: {
        write: (path, data) => new Promise(resolve => {
            const id = ++_msgId;
            _pending.set(id, resolve);
            self.postMessage({ type: 'opfs', op: 'write', id, path, data });
        }),
        read: (path) => new Promise(resolve => {
            const id = ++_msgId;
            _pending.set(id, resolve);
            self.postMessage({ type: 'opfs', op: 'read', id, path });
        })
    }
});

const config = getConfig();
_exports = await getAssemblyExports(config.mainAssemblyName);

// ─── Handle messages from main thread ───────────────────────

self.onmessage = async (e) => {
    const { type } = e.data;
    switch (type) {
        case 'navigate':
            _currentPath = e.data.path;
            if (_exports) await _exports.Cepha.JsExports.Navigate(e.data.path);
            break;
        case 'submit':
            if (_exports) await _exports.Cepha.JsExports.SubmitForm(e.data.action, JSON.stringify(e.data.data));
            break;
        case 'hub-connect':
            if (_exports) {
                const connId = await _exports.Cepha.JsExports.HubConnect(e.data.hubName);
                self.postMessage({ type: 'hub-result', id: e.data.id, result: connId });
            }
            break;
        case 'hub-disconnect':
            if (_exports) await _exports.Cepha.JsExports.HubDisconnect(e.data.hubName, e.data.connId);
            break;
        case 'auth-sync':
            if (e.data.path) _currentPath = e.data.path;
            if (_exports) await _exports.Cepha.JsExports.SyncAuth();
            break;
        case 'hub-invoke':
            if (_exports) {
                const result = await _exports.Cepha.JsExports.HubInvoke(
                    e.data.hubName, e.data.method, e.data.connId, e.data.argsJson);
                self.postMessage({ type: 'hub-result', id: e.data.id, result });
            }
            break;
        case 'fetch':
            if (_exports) {
                const fetchResult = await _exports.Cepha.JsExports.FetchRoute(e.data.path);
                self.postMessage({ type: 'fetch-result', id: e.data.id, result: fetchResult });
            }
            break;
        case 'cephaDb-result':
        case 'opfs-result': {
            const resolver = _pending.get(e.data.id);
            if (resolver) {
                _pending.delete(e.data.id);
                resolver(e.data.result ?? '');
            }
            break;
        }
    }
};

self.postMessage({ type: 'runtime-ready' });

// Boot the .NET app (Program.Main → CephaApp.RunAsync)
runMain().catch(err => console.error('🧬 runMain failed:', err));
