// 🧬 NetWasmMvc.SDK — Main Thread (Display Surface)
// This thread is a BLIND display surface — a video screen.
// The Worker (plasma source) pushes frames via postMessage.
// All intelligence, MVC, EF Core, and SQLite run in the Worker.
// main.js only renders what it's told — it has ZERO page awareness.

console.log('%c🧬 NetWasmMvc.SDK — Display Surface', 'color: #667eea; font-weight: bold');

// ─── CephaLoader: Native Loading System ──────────────────────

const CephaLoader = (() => {
    // Context-aware messages based on controller/action path
    const messages = {
        '/identity/account/login':    { text: '🔐 Signing in...', sub: 'Verifying credentials' },
        '/identity/account/register': { text: '🧬 Creating your account...', sub: 'Setting up your profile' },
        '/identity/account/logout':   { text: '👋 Signing out...', sub: 'Clearing session' },
        '/identity/account/profile':  { text: '📋 Loading profile...', sub: 'Fetching data' },
    };

    const defaultNav = { text: '🧬 Loading...', sub: 'Cepha' };
    const defaultSubmit = { text: '⚡ Processing...', sub: 'Please wait' };

    let overlay = null;
    let navBar = null;
    let _activeForm = null;

    function init() {
        // Overlay (form submissions)
        overlay = document.createElement('div');
        overlay.className = 'cepha-loader-overlay';
        overlay.innerHTML = `
            <div class="cepha-loader-sigil">
                <span class="cepha-loader-node"></span>
                <span class="cepha-loader-node"></span>
                <span class="cepha-loader-node"></span>
                <span class="cepha-loader-node"></span>
                <div class="cepha-loader-veins"></div>
            </div>
            <div class="cepha-loader-message"></div>
            <div class="cepha-loader-sub"></div>`;
        document.body.appendChild(overlay);

        // Nav progress bar (link navigation)
        navBar = document.createElement('div');
        navBar.className = 'cepha-nav-progress';
        document.body.appendChild(navBar);
    }

    function getMessage(action, isSubmit) {
        const key = action.toLowerCase().replace(/\/$/, '');
        if (messages[key]) return messages[key];

        // Auto-detect from path segments
        const parts = key.split('/').filter(Boolean);
        const verb = parts[parts.length - 1] || '';
        const area = parts[0] || '';

        if (isSubmit) {
            if (verb === 'login' || verb === 'signin') return { text: '🔐 Signing in...', sub: 'Verifying credentials' };
            if (verb === 'register' || verb === 'signup') return { text: '🧬 Creating account...', sub: 'Setting up' };
            if (verb === 'delete' || verb === 'remove') return { text: '🗑️ Removing...', sub: 'Processing' };
            if (verb === 'save' || verb === 'update' || verb === 'edit') return { text: '💾 Saving...', sub: 'Updating data' };
            if (verb === 'search' || verb === 'find') return { text: '🔍 Searching...', sub: 'Querying' };
            if (verb === 'send' || verb === 'submit') return { text: '📤 Sending...', sub: 'Processing' };
            return defaultSubmit;
        }
        return defaultNav;
    }

    function showOverlay(action, isSubmit = true) {
        if (!overlay) init();
        const msg = getMessage(action, isSubmit);
        overlay.querySelector('.cepha-loader-message').textContent = msg.text;
        overlay.querySelector('.cepha-loader-sub').textContent = msg.sub;
        overlay.classList.add('active');
    }

    function hideOverlay() {
        if (overlay) overlay.classList.remove('active');
        releaseForm();
    }

    function lockForm(form) {
        if (!form) return;
        _activeForm = form;
        form.classList.add('cepha-processing');
    }

    function releaseForm() {
        if (_activeForm) {
            _activeForm.classList.remove('cepha-processing');
            _activeForm = null;
        }
    }

    function startNav() {
        if (!navBar) init();
        navBar.classList.remove('done');
        navBar.classList.add('active');
    }

    function endNav() {
        if (!navBar) return;
        navBar.classList.remove('active');
        navBar.classList.add('done');
        setTimeout(() => navBar.classList.remove('done'), 500);
    }

    return { init, showOverlay, hideOverlay, lockForm, releaseForm, startNav, endNav };
})();

// Init loader DOM elements
CephaLoader.init();

// ─── Boot Runtime Worker (.NET runs here) ────────────────────

const worker = new Worker('./cepha-runtime-worker.js', { type: 'module' });

// Wait for worker to finish dotnet.create()
await new Promise(resolve => {
    const h = (e) => {
        if (e.data.type === 'created') { worker.removeEventListener('message', h); resolve(); }
    };
    worker.addEventListener('message', h);
});

// Send init: current path + localStorage snapshot + resolved dotnet.js URL
const storageSnapshot = {};
for (let i = 0; i < localStorage.length; i++) {
    const k = localStorage.key(i);
    if (k) storageSnapshot[k] = localStorage.getItem(k);
}
// Resolve dotnet.js URL from import map (handles fingerprinted filenames in dev/prod)
let dotnetUrl = './_framework/dotnet.js';
try {
    const el = document.querySelector('script[type="importmap"]');
    if (el) {
        const map = JSON.parse(el.textContent);
        if (map.imports && map.imports['./_framework/dotnet.js']) dotnetUrl = map.imports['./_framework/dotnet.js'];
    }
} catch (_) {}
worker.postMessage({ type: 'init', path: location.pathname || '/', storage: storageSnapshot, fingerprint: navigator.userAgent, dotnetUrl });

// ─── CephaKit Client (runs on main thread — needs fetch + DOM) ──

window.CephaClient = {
    serverUrl: null,
    connected: false,
    async discover(urls) {
        const candidates = urls || [
            `${location.protocol}//${location.hostname}:3000`,
            'http://localhost:3000'
        ];
        for (const url of candidates) {
            try {
                const res = await fetch(`${url}/_cepha/info`, { signal: AbortSignal.timeout(2000) });
                if (res.ok) {
                    this.serverUrl = url;
                    this.connected = true;
                    console.log(`%c🧬 Cepha server: ${url}`, 'color: #28a745; font-weight: bold');
                    return await res.json();
                }
            } catch { /* next */ }
        }
        console.log('%c🧬 Cepha: offline mode (Worker-only)', 'color: #ffc107');
        return null;
    },
    async fetch(method, path, body) {
        if (!this.connected) return null;
        const opts = { method, headers: { 'Content-Type': 'application/json' } };
        if (body && ['POST', 'PUT', 'PATCH'].includes(method))
            opts.body = typeof body === 'string' ? body : JSON.stringify(body);
        return await (await fetch(`${this.serverUrl}${path}`, opts)).json();
    }
};

// ─── Cross-Tab Auth Sync (BroadcastChannel) ─────────────────

const _authChannel = new BroadcastChannel('cepha-auth');
let _authSyncTimer = null;
_authChannel.onmessage = (e) => {
    // Debounce: if multiple auth-sync messages arrive quickly, process only the last
    clearTimeout(_authSyncTimer);
    _authSyncTimer = setTimeout(() => {
        worker.postMessage({ type: 'auth-sync', path: location.pathname });
    }, 50);
};

// ─── Frame Buffer + Render Loop (Video Stream Architecture) ──
// The main thread is a blind display surface in a permanent rAF loop.
// The Worker is the plasma source — it pushes frames, we just display.

const _frameBuffer = [];
let _frameSeq = 0;
let _rafScheduled = false;

function pushFrame(frame) {
    frame._seq = ++_frameSeq;
    _frameBuffer.push(frame);
    if (!_rafScheduled) {
        _rafScheduled = true;
        requestAnimationFrame(renderLoop);
    }
}

function renderLoop() {
    // Drain all queued frames in sequence (one rAF pass)
    while (_frameBuffer.length > 0) {
        applyFrame(_frameBuffer.shift());
    }
    _rafScheduled = false;
}

function applyFrame(frame) {
    const el = document.querySelector(frame.selector);
    if (!el) return;

    // L4: Signal CephaSecurity that a Worker frame is being applied
    if (typeof CephaSecurity !== 'undefined') CephaSecurity.beginWorkerFrame();

    switch (frame.op) {
        case 'setInnerHTML':
            el.innerHTML = frame.value;
            // Execute <script> tags — innerHTML doesn't run them natively
            activateScripts(el);
            CephaLoader.hideOverlay();
            CephaLoader.endNav();
            break;
        case 'setInnerText':  el.innerText = frame.value; break;
        case 'setAttribute':  el.setAttribute(frame.attr, frame.value); break;
        case 'addClass':      el.classList.add(frame.value); break;
        case 'removeClass':   el.classList.remove(frame.value); break;
        case 'show':          el.style.display = ''; break;
        case 'hide':          el.style.display = 'none'; break;
        case 'streamStart':
            el.innerHTML = '';
            break;
        case 'streamAppend':
            el.insertAdjacentHTML('beforeend', frame.value);
            break;
        case 'streamEnd':
            activateScripts(el);
            CephaLoader.hideOverlay();
            CephaLoader.endNav();
            break;
    }

    // L4: End Worker frame — CephaSecurity handles debounced observer reconnection internally
    if (typeof CephaSecurity !== 'undefined') CephaSecurity.endWorkerFrame();
    if (typeof CephaMaterial !== 'undefined' && frame.selector === '#app') {
        CephaMaterial.onWorkerFrameApplied(el);
    }
}

// Activate resources from innerHTML (which doesn't run scripts or load stylesheets natively).
// Mimics default browser page-load behaviour:
//   1. Promote <link rel="stylesheet"> to <head> and wait for them to load (render-blocking, like a real <head>).
//   2. Promote <style> tags to <head> for reliable application.
//   3. Process <script> tags in document order — external scripts wait for
//      onload before the next script runs, inline scripts execute immediately.
function activateScripts(container) {
    // ── CSS: promote <style> tags to <head> ──
    document.querySelectorAll('style[data-cepha-view]').forEach(s => s.remove());
    for (const s of [...container.querySelectorAll('style')]) {
        s.setAttribute('data-cepha-view', '');
        s.remove();
        document.head.appendChild(s);
    }
    // ── CSS: promote <link rel="stylesheet"> to <head> and wait ──
    document.querySelectorAll('link[data-cepha-view]').forEach(l => l.remove());
    const cssLinks = [...container.querySelectorAll('link[rel="stylesheet"]')];
    let cssRemaining = cssLinks.length;

    function runScripts() {
        // Mark as worker-frame scripts so CephaSecurity doesn't flag as injection
        if (typeof CephaSecurity !== 'undefined' && !CephaSecurity.isFrameActive()) {
            CephaSecurity.beginWorkerFrame();
            var needEnd = true;
        }
        const scripts = [...container.querySelectorAll('script')];
        let i = 0;
        function next() {
            if (i >= scripts.length) {
                if (needEnd && typeof CephaSecurity !== 'undefined') CephaSecurity.endWorkerFrame();
                return;
            }
            const old = scripts[i++];
            const live = document.createElement('script');
            for (const attr of old.attributes) live.setAttribute(attr.name, attr.value);
            live.textContent = old.textContent;
            if (live.src) {
                live.onload = live.onerror = next;
            }
            old.parentNode.replaceChild(live, old);
            if (!live.src) next();
        }
        next();
    }

    if (cssRemaining === 0) { runScripts(); return; }

    for (const l of cssLinks) {
        const live = document.createElement('link');
        for (const attr of l.attributes) live.setAttribute(attr.name, attr.value);
        live.setAttribute('data-cepha-view', '');
        l.remove();
        live.onload = live.onerror = () => { if (--cssRemaining === 0) runScripts(); };
        document.head.appendChild(live);
    }
}

// ─── Handle Worker Messages (render HTML results) ────────────

let _hubMsgId = 0;
const _hubPending = new Map();
const _fetchPending = new Map();
let _fetchMsgId = 0;

worker.onmessage = (e) => {
    const d = e.data;
    switch (d.type) {
        // ── DOM rendering → Frame Buffer (display surface) ───
        case 'dom':
            pushFrame(d);
            break;
        // ── Navigation ───────────────────────────────────
        case 'pushState':
            history.pushState({}, '', d.path);
            break;

        // ── localStorage sync ────────────────────────────
        case 'storage':
            if (d.op === 'set') localStorage.setItem(d.key, d.value);
            else if (d.op === 'remove') localStorage.removeItem(d.key);
            break;

        // ── CephaKit discovery (delegated from worker) ───
        case 'cephakit':
            window.CephaClient.discover([
                `https://${location.hostname}:${d.port}`,
                `http://${location.hostname}:${d.port}`
            ]);
            break;

        // ── Cross-tab auth broadcast ─────────────────────
        case 'auth-changed':
            _authChannel.postMessage({ action: d.action, ts: Date.now() });
            break;

        // ── OPFS DB bridge (worker ↔ OPFS data worker) ──
        case 'cephaDb':
            if (d.op === 'restore') {
                window.CephaData.importDb().then(result => {
                    let base64 = '';
                    if (result?.data && result.data.byteLength > 100) {
                        const bytes = new Uint8Array(result.data);
                        const chunks = [];
                        for (let i = 0; i < bytes.length; i += 8192)
                            chunks.push(String.fromCharCode.apply(null, bytes.subarray(i, i + 8192)));
                        base64 = btoa(chunks.join(''));
                    }
                    worker.postMessage({ type: 'cephaDb-result', id: d.id, result: base64 });
                }).catch(() => {
                    worker.postMessage({ type: 'cephaDb-result', id: d.id, result: '' });
                });
            } else if (d.op === 'persist') {
                const binary = atob(d.base64);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
                window.CephaData.exportDb(bytes.buffer).then(() => {
                    worker.postMessage({ type: 'cephaDb-result', id: d.id });
                }).catch(() => {
                    worker.postMessage({ type: 'cephaDb-result', id: d.id });
                });
            }
            break;

        // ── OPFS generic read/write (worker ↔ OPFS data worker) ──
        case 'opfs':
            if (d.op === 'write') {
                window.CephaData.write(d.path, d.data).then(() => {
                    worker.postMessage({ type: 'opfs-result', id: d.id, result: true });
                }).catch(() => {
                    worker.postMessage({ type: 'opfs-result', id: d.id, result: false });
                });
            } else if (d.op === 'read') {
                window.CephaData.read(d.path, true).then(data => {
                    worker.postMessage({ type: 'opfs-result', id: d.id, result: data ?? null });
                }).catch(() => {
                    worker.postMessage({ type: 'opfs-result', id: d.id, result: null });
                });
            }
            break;

        // ── File download ────────────────────────────────
        case 'download': {
            const bin = atob(d.b64);
            const arr = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
            const blob = new Blob([arr], { type: d.mime });
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = d.name;
            a.click();
            URL.revokeObjectURL(a.href);
            break;
        }

        // ── SignalR events → cross-tab broadcast ─────────
        case 'signalr': {
            const key = `${d.hubName}:${d.method}`.toLowerCase();
            const handlers = window.__signalrHandlers?.[key] || [];
            const args = JSON.parse(d.argsJson || '[]');
            handlers.forEach(fn => { try { fn(...args); } catch {} });
            window.__signalrChannel?.postMessage({
                hubName: d.hubName, method: d.method, connectionId: d.connectionId, argsJson: d.argsJson
            });
            break;
        }

        // ── SignalR hub operation results ─────────────────
        case 'hub-result': {
            const resolver = _hubPending.get(d.id);
            if (resolver) { _hubPending.delete(d.id); resolver(d.result); }
            break;
        }

        // ── Fetch route results (cephaFetch API) ─────────
        case 'fetch-result': {
            const fr = _fetchPending.get(d.id);
            if (fr) { _fetchPending.delete(d.id); fr(d.result); }
            break;
        }
    }
};

// ─── SPA Router: intercept link clicks ───────────────────────

document.addEventListener('click', (e) => {
    const link = e.target.closest('a[href]');
    if (!link) return;
    const href = link.getAttribute('href');
    if (!href || href.startsWith('http') || href.startsWith('//') || href.startsWith('#')) return;
    if (link.getAttribute('target') === '_blank') return;
    if (/\.\w{2,5}$/.test(href)) return;
    e.preventDefault();
    CephaLoader.startNav();
    history.pushState({}, '', href);
    worker.postMessage({ type: 'navigate', path: href });
});

// ─── SPA Router: handle back/forward ─────────────────────────

addEventListener('popstate', () => {
    CephaLoader.startNav();
    worker.postMessage({ type: 'navigate', path: location.pathname });
});

// ─── Form submission handler ─────────────────────────────────

document.addEventListener('submit', (e) => {
    const form = e.target;
    if (!form || form.tagName !== 'FORM') return;
    e.preventDefault();
    const action = form.getAttribute('action') || location.pathname;
    const data = Object.fromEntries(new FormData(form).entries());

    // Merge secure field values (Shadow DOM password/identity fields)
    if (typeof CephaSecurity !== 'undefined') {
        Object.assign(data, CephaSecurity.getSecureFormData());
    }

    // Lock form + show loader
    CephaLoader.lockForm(form);
    CephaLoader.showOverlay(action, true);

    // Route through CephaKit server when connected
    if (window.CephaClient?.connected) {
        fetch(`${window.CephaClient.serverUrl}${action}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(data)
        })
        .then(r => r.json())
        .then(res => {
            CephaLoader.hideOverlay();
            if (res.statusCode === 302 && res.body) {
                history.pushState({}, '', res.body);
                worker.postMessage({ type: 'navigate', path: res.body });
            } else if (res.body) {
                document.querySelector('#app').innerHTML = res.body;
            }
        })
        .catch(() => {
            worker.postMessage({ type: 'submit', action, data });
        });
        return;
    }

    // Worker handles it (non-blocking — all .NET runs off main thread)
    worker.postMessage({ type: 'submit', action, data });
});

// ─── SignalR Client (proxy to worker) ────────────────────────

window.__signalrHandlers = {};
window.__signalrChannel = new BroadcastChannel('cepha-signalr');
window.__signalrChannel.onmessage = ({ data }) => {
    const key = `${data.hubName}:${data.method}`.toLowerCase();
    const args = JSON.parse(data.argsJson || '[]');
    (window.__signalrHandlers[key] || []).forEach(fn => { try { fn(...args); } catch {} });
};

window.SignalR = {
    connections: {},
    async connect(hubName) {
        const id = ++_hubMsgId;
        const connId = await new Promise(resolve => {
            _hubPending.set(id, resolve);
            worker.postMessage({ type: 'hub-connect', hubName, id });
        });
        this.connections[hubName] = connId;
        return connId;
    },
    async disconnect(hubName) {
        const connId = this.connections[hubName];
        if (connId) {
            worker.postMessage({ type: 'hub-disconnect', hubName, connId });
            delete this.connections[hubName];
        }
    },
    async invoke(hubName, method, ...args) {
        const id = ++_hubMsgId;
        return new Promise(resolve => {
            _hubPending.set(id, resolve);
            worker.postMessage({
                type: 'hub-invoke', hubName, method,
                connId: this.connections[hubName] || '',
                argsJson: JSON.stringify(args), id
            });
        });
    },
    on(hubName, method, callback) {
        const key = `${hubName}:${method}`.toLowerCase();
        if (!window.__signalrHandlers[key]) window.__signalrHandlers[key] = [];
        window.__signalrHandlers[key].push(callback);
    },
    off(hubName, method) {
        delete window.__signalrHandlers[`${hubName}:${method}`.toLowerCase()];
    }
};

// ─── CephaFetch: Route fetch through WASM Worker ─────────────

window.cephaFetch = function(url) {
    const id = ++_fetchMsgId;
    return new Promise(resolve => {
        _fetchPending.set(id, resolve);
        worker.postMessage({ type: 'fetch', path: url, id });
    });
};

// ─── CephaData: OPFS Worker Bridge ──────────────────────────

window.CephaData = (() => {
    let opfsWorker = null;
    let ready = false;
    let msgId = 0;
    const pending = new Map();
    const subscribers = new Map();
    const channel = new BroadcastChannel('cepha-data');

    function init() {
        if (opfsWorker) return;
        try {
            opfsWorker = new Worker('./cepha-data-worker.js');
            opfsWorker.onmessage = ({ data }) => {
                if (data.type === 'ready') {
                    ready = true;
                    send('init').then(() =>
                        console.log('%c🧬 CephaData: OPFS worker ready', 'color: #48bb78; font-weight: bold')
                    );
                    return;
                }
                const p = pending.get(data.id);
                if (p) {
                    pending.delete(data.id);
                    data.type === 'error' ? p.reject(new Error(data.error)) : p.resolve(data.result);
                }
            };
            opfsWorker.onerror = (e) => console.error('CephaData worker error:', e.message);
        } catch {
            console.warn('🧬 CephaData: Worker unavailable, OPFS disabled');
        }
    }

    function send(type, payload) {
        return new Promise((resolve, reject) => {
            if (!opfsWorker) { reject(new Error('Worker not initialized')); return; }
            const id = ++msgId;
            pending.set(id, { resolve, reject });
            opfsWorker.postMessage({ id, type, payload });
        });
    }

    channel.onmessage = ({ data }) => {
        (subscribers.get(data.event) || []).forEach(fn => { try { fn(data.detail); } catch {} });
    };

    function notify(event, detail) {
        channel.postMessage({ event, detail });
        (subscribers.get(event) || []).forEach(fn => { try { fn(detail); } catch {} });
    }

    init();

    return {
        write: (path, data) => send('write', { path, data }),
        read: (path, asText) => send('read', { path, asText }),
        delete: (path) => send('delete', { path }),
        list: (path) => send('list', { path }),
        exportDb: (data) => send('db-export', { data }),
        importDb: () => send('db-import'),
        enqueue: (change) => send('enqueue', change).then(count => { notify('pending-change', { count, change }); return count; }),
        dequeue: () => send('dequeue'),
        pendingCount: () => send('pending-count'),
        stats: () => send('stats'),
        on(event, fn) { if (!subscribers.has(event)) subscribers.set(event, []); subscribers.get(event).push(fn); },
        off(event, fn) { const s = subscribers.get(event); if (s) subscribers.set(event, s.filter(f => f !== fn)); },
        notify,
        get isEncrypted() { return false; },
        get isPremium() { return false; }
    };
})();

// ─── CephaSysLog + CephaSecurity + CephaMaterial Initialization ────────────

if (typeof CephaSysLog !== 'undefined') {
    CephaSysLog.init(worker);
}

if (typeof CephaSecurity !== 'undefined') {
    CephaSecurity.init('#app', worker, { mode: 'development', enforcement: false });
    // Start L7 MaterialDb integrity monitor (checks every 10s when enforcement is ON)
    CephaSecurity.startMaterialDbMonitor();
}

if (typeof CephaMaterialDb !== 'undefined') {
    CephaMaterialDb.init();
}

if (typeof CephaMaterial !== 'undefined') {
    CephaMaterial.init(worker);
    CephaMaterial.startStreamListener('#app', 500);
    CephaMaterial.createSecurityIndicator();
}

// ─── Service Worker Registration ─────────────────────────────
// Only registers if <meta name="cepha-sw" content="true"> is present in the HTML.
// This avoids a HEAD fetch that logs a 404 in the console when no SW exists.

if ('serviceWorker' in navigator) {
    const swMeta = document.querySelector('meta[name="cepha-sw"][content="true"]');
    if (swMeta) navigator.serviceWorker.register('service-worker.js').catch(() => {});
}
