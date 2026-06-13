// 🎨 CephaMaterialDb — Encrypted Design System Database
// ═══════════════════════════════════════════════════════════════════════════
// ⚠️ COMPLIANCE: Before modifying this file, review SDK_DEVELOPMENT_STANDARDS.md
// ═══════════════════════════════════════════════════════════════════════════
//
// Database-backed design system providing persistent, queryable storage for
// visual design tokens, component metadata, and DOM layout instructions.
//
// Architecture:
//   • 3-table schema: Components → ViewMeta → DomInstructions
//   • AES-256-GCM encrypted at rest — one sealed blob file per table in OPFS
//   • Storage root: cepha/material-db/{components,viewMeta,domInstructions}.enc
//   • Encryption key stored in OPFS (cepha/keys/) — invisible in DevTools
//   • Envelope: [magic 0xCE][ver 0x02][iv 12B][ciphertext + GCM tag]
//   • IndexedDB is NOT used — legacy DB ('cepha-material-db' v1) is auto-migrated
//     into OPFS on first run and then deleted.
//   • Backward compat: ephemeral key fallback if OPFS unavailable
//   • Theme-aware: multiple theme presets stored per component
//   • Event emitter for real-time UI updates (no WebSocket needed)
//   • CephaSecurity integration: DOM integrity snapshots stored per component
//   • Worker-aware: feeds the "LCD screen" frame pipeline via postMessage
//
// Storage Model:
//   components      — Core element registry (id, name, type, route, hierarchy)
//   viewMeta        — Visual properties (colors, fonts, spacing, position, size)
//   domInstructions — Behavioral specs (animations, hover, integrity hash)
//
// Theme System:
//   Each viewMeta record is scoped to a (componentId, themeId) pair.
//   Switching themes = changing the active themeId → CSS vars regenerated.
//   Built-in themes: 'light', 'dark'. Custom themes: user-defined.
//
// Integration:
//   CephaMaterial.js  → reads tokens, applies CSS custom properties
//   CephaSecurity.js  → reads integrity hashes, verifies DOM state
//   Worker renderer   → reads component specs, generates consistent HTML
//   CephaSysLog.js    → logs design mutations for audit/anomaly detection

const CephaMaterialDb = (() => {
    'use strict';

    // ── State ──
    let _opfs = null;             // OPFS dir handle at cepha/material-db/
    let _cryptoKey = null;
    let _initialized = false;
    let _initPromise = null;
    let _keyPersisted = false;
    let _activeTheme = 'light';
    // Blob cache per store — the in-memory authoritative copy
    const _storeBlob = {
        components: null,         // Map<id, record>
        viewMeta: null,
        domInstructions: null
    };
    const _cache = {
        components: new Map(),
        viewMeta: new Map(),
        domInstructions: new Map()
    };
    const _listeners = new Map();
    const _themeListeners = new Set();

    // Store keys double as OPFS basenames (`.enc` is appended).
    const STORES = {
        COMPONENTS: 'components',
        VIEW_META: 'viewMeta',
        DOM_INSTRUCTIONS: 'domInstructions'
    };
    const OPFS_DB_DIR = 'cepha/material-db';
    const BLOB_MAGIC = 0xCE;
    const BLOB_VERSION = 0x02;
    // Legacy IndexedDB — kept only for one-shot migration.
    const LEGACY_DB_NAME = 'cepha-material-db';

    // ── Default Design Tokens ──
    // These define the baseline Cepha Material design system.
    // All values are overridable per-component via viewMeta records.

    const DEFAULT_TOKENS = {
        light: {
            primary: '#667eea', primaryDark: '#5a67d8', primaryLight: '#a3bffa',
            primaryAlpha: 'rgba(102,126,234,0.15)',
            secondary: '#764ba2', secondaryDark: '#6b46a3',
            success: '#48bb78', warning: '#ed8936', danger: '#f56565', info: '#4299e1',
            bg: '#ffffff', surface: '#f7fafc', surfaceElevated: '#ffffff',
            text: '#1a202c', textSecondary: '#718096',
            placeholder: '#a0aec0', label: '#4a5568',
            border: '#e2e8f0', divider: '#edf2f7',
            inputBg: '#ffffff', inputDisabledBg: '#f7fafc',
            focusRing: 'rgba(102,126,234,0.25)',
            fontFamily: "'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif",
            fontMono: "'JetBrains Mono','Fira Code','Cascadia Code',monospace",
            fontSize: '16px', fontWeight: '400',
            radiusSm: '4px', radius: '8px', radiusLg: '12px', radiusXl: '16px',
            shadowSm: '0 1px 2px rgba(0,0,0,0.05)',
            shadow: '0 1px 3px rgba(0,0,0,0.1),0 1px 2px rgba(0,0,0,0.06)',
            shadowMd: '0 4px 6px rgba(0,0,0,0.07),0 2px 4px rgba(0,0,0,0.06)',
            shadowLg: '0 10px 15px rgba(0,0,0,0.1),0 4px 6px rgba(0,0,0,0.05)',
            transition: '0.2s cubic-bezier(0.4,0,0.2,1)',
            transitionFast: '0.15s cubic-bezier(0.4,0,0.2,1)'
        },
        dark: {
            primary: '#667eea', primaryDark: '#5a67d8', primaryLight: '#a3bffa',
            primaryAlpha: 'rgba(102,126,234,0.2)',
            secondary: '#764ba2', secondaryDark: '#6b46a3',
            success: '#48bb78', warning: '#ed8936', danger: '#f56565', info: '#4299e1',
            bg: '#0f1219', surface: '#1a1f35', surfaceElevated: '#252b45',
            text: '#e2e8f0', textSecondary: '#94a3b8',
            placeholder: '#64748b', label: '#94a3b8',
            border: '#2a3050', divider: '#1e2440',
            inputBg: '#1a1f35', inputDisabledBg: '#0f1219',
            focusRing: 'rgba(102,126,234,0.35)',
            fontFamily: "'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif",
            fontMono: "'JetBrains Mono','Fira Code','Cascadia Code',monospace",
            fontSize: '16px', fontWeight: '400',
            radiusSm: '4px', radius: '8px', radiusLg: '12px', radiusXl: '16px',
            shadowSm: '0 0 8px rgba(102,126,234,0.08)',
            shadow: '0 0 15px rgba(102,126,234,0.06), 0 0 30px rgba(118,75,162,0.04)',
            shadowMd: '0 0 20px rgba(102,126,234,0.08), 0 0 40px rgba(118,75,162,0.05)',
            shadowLg: '0 0 30px rgba(102,126,234,0.1), 0 0 60px rgba(118,75,162,0.06)',
            transition: '0.2s cubic-bezier(0.4,0,0.2,1)',
            transitionFast: '0.15s cubic-bezier(0.4,0,0.2,1)'
        }
    };

    // Token-to-CSS-variable mapping
    const TOKEN_CSS_MAP = {
        primary: '--cepha-primary', primaryDark: '--cepha-primary-dark',
        primaryLight: '--cepha-primary-light', primaryAlpha: '--cepha-primary-alpha',
        secondary: '--cepha-secondary', secondaryDark: '--cepha-secondary-dark',
        success: '--cepha-success', warning: '--cepha-warning',
        danger: '--cepha-danger', info: '--cepha-info',
        bg: '--cepha-bg', surface: '--cepha-surface',
        surfaceElevated: '--cepha-surface-elevated',
        text: '--cepha-text', textSecondary: '--cepha-text-secondary',
        placeholder: '--cepha-placeholder', label: '--cepha-label',
        border: '--cepha-border', divider: '--cepha-divider',
        inputBg: '--cepha-input-bg', inputDisabledBg: '--cepha-input-disabled-bg',
        focusRing: '--cepha-focus-ring',
        fontFamily: '--cepha-font', fontMono: '--cepha-font-mono',
        radiusSm: '--cepha-radius-sm', radius: '--cepha-radius',
        radiusLg: '--cepha-radius-lg', radiusXl: '--cepha-radius-xl',
        shadowSm: '--cepha-shadow-sm', shadow: '--cepha-shadow',
        shadowMd: '--cepha-shadow-md', shadowLg: '--cepha-shadow-lg',
        transition: '--cepha-transition', transitionFast: '--cepha-transition-fast'
    };

    // ── Encryption (AES-256-GCM, OPFS-persisted key) ──
    // Key is stored in OPFS (cepha/keys/material-db.key) so it survives
    // page reloads. This is "durable local encryption" — the key is
    // browser-origin-scoped, not derived from a server secret.

    const OPFS_KEY_DIR = 'cepha/keys';
    const OPFS_KEY_FILE = 'material-db.key';

    async function initCrypto() {
        try {
            let root = await navigator.storage.getDirectory();
            // Navigate into cepha/keys/ (create if needed)
            for (const seg of OPFS_KEY_DIR.split('/')) {
                root = await root.getDirectoryHandle(seg, { create: true });
            }

            try {
                // Load existing key from OPFS
                const fh = await root.getFileHandle(OPFS_KEY_FILE);
                const file = await fh.getFile();
                const raw = await file.arrayBuffer();
                if (raw.byteLength !== 32) throw new Error('corrupt key (' + raw.byteLength + 'B)');
                _cryptoKey = await crypto.subtle.importKey(
                    'raw', raw, { name: 'AES-GCM', length: 256 }, true, ['encrypt', 'decrypt']
                );
                _keyPersisted = true;
            } catch (loadErr) {
                // Only generate a new key when the file truly does not exist
                if (loadErr.name === 'NotFoundError') {
                    _cryptoKey = await crypto.subtle.generateKey(
                        { name: 'AES-GCM', length: 256 }, true, ['encrypt', 'decrypt']
                    );
                    const exported = await crypto.subtle.exportKey('raw', _cryptoKey);
                    const fh = await root.getFileHandle(OPFS_KEY_FILE, { create: true });
                    const writable = await fh.createWritable();
                    await writable.write(exported);
                    await writable.close();
                    _keyPersisted = true;
                } else {
                    // Corrupt or locked key — log and fall back to ephemeral
                    console.warn('CephaMaterialDb: key load failed (' + loadErr.message + ') — using ephemeral key');
                    _cryptoKey = await crypto.subtle.generateKey(
                        { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']
                    );
                }
            }
        } catch (opfsErr) {
            // OPFS unavailable (non-secure context, unsupported browser) — ephemeral key
            _cryptoKey = await crypto.subtle.generateKey(
                { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']
            );
        }
    }

    async function encrypt(data) {
        if (!_cryptoKey) return data;
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const encoded = new TextEncoder().encode(JSON.stringify(data));
        const ciphertext = await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv }, _cryptoKey, encoded
        );
        return { _encrypted: true, iv, ciphertext };
    }

    async function decrypt(blob) {
        if (!blob || !blob._encrypted) return blob;
        const plaintext = await crypto.subtle.decrypt(
            { name: 'AES-GCM', iv: blob.iv }, _cryptoKey, blob.ciphertext
        );
        return JSON.parse(new TextDecoder().decode(plaintext));
    }

    // ── OPFS Storage ── (replaces IndexedDB)
    // Each store is a single AES-GCM-sealed blob file under cepha/material-db/.
    // On init we migrate any legacy IndexedDB records into OPFS then delete the DB.

    async function openOpfs() {
        let root = await navigator.storage.getDirectory();
        for (const seg of OPFS_DB_DIR.split('/')) {
            root = await root.getDirectoryHandle(seg, { create: true });
        }
        return root;
    }

    async function _encryptBlob(plaintextBytes) {
        if (!_cryptoKey) return plaintextBytes;
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, _cryptoKey, plaintextBytes);
        const out = new Uint8Array(2 + 12 + ct.byteLength);
        out[0] = BLOB_MAGIC;
        out[1] = BLOB_VERSION;
        out.set(iv, 2);
        out.set(new Uint8Array(ct), 14);
        return out;
    }

    async function _decryptBlob(bytes) {
        if (!_cryptoKey) return bytes;
        if (bytes.length < 14 || bytes[0] !== BLOB_MAGIC || bytes[1] !== BLOB_VERSION) {
            // Unsealed — return as-is (caller will attempt JSON parse)
            return bytes;
        }
        const iv = bytes.slice(2, 14);
        const ct = bytes.slice(14);
        const pt = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, _cryptoKey, ct);
        return new Uint8Array(pt);
    }

    async function _loadStoreBlob(storeKey) {
        if (_storeBlob[storeKey]) return _storeBlob[storeKey];
        const fileName = storeKey + '.enc';
        try {
            const fh = await _opfs.getFileHandle(fileName);
            const file = await fh.getFile();
            if (file.size === 0) { _storeBlob[storeKey] = new Map(); return _storeBlob[storeKey]; }
            const raw = new Uint8Array(await file.arrayBuffer());
            const plaintext = await _decryptBlob(raw);
            const records = JSON.parse(new TextDecoder().decode(plaintext));
            const map = new Map();
            for (const r of records) if (r && r.id) map.set(r.id, r);
            _storeBlob[storeKey] = map;
            return map;
        } catch (e) {
            if (e && (e.name === 'NotFoundError' || e.code === 8)) {
                _storeBlob[storeKey] = new Map();
                return _storeBlob[storeKey];
            }
            console.warn('CephaMaterialDb: _loadStoreBlob(' + storeKey + ') failed', e);
            _storeBlob[storeKey] = new Map();
            return _storeBlob[storeKey];
        }
    }

    async function _saveStoreBlob(storeKey) {
        const map = _storeBlob[storeKey] || new Map();
        const records = Array.from(map.values());
        const plaintext = new TextEncoder().encode(JSON.stringify(records));
        const sealed = await _encryptBlob(plaintext);
        const fileName = storeKey + '.enc';
        const fh = await _opfs.getFileHandle(fileName, { create: true });
        const writable = await fh.createWritable();
        await writable.write(sealed);
        await writable.close();
    }

    async function _migrateLegacyIndexedDb() {
        if (typeof indexedDB === 'undefined') return 0;
        try {
            if (typeof indexedDB.databases === 'function') {
                const dbs = await indexedDB.databases();
                if (!dbs.some(d => d.name === LEGACY_DB_NAME)) return 0;
            }
        } catch (_) { /* Safari: no databases() — fall through and probe via open */ }

        return new Promise((resolve) => {
            let created = false;
            const req = indexedDB.open(LEGACY_DB_NAME);
            req.onerror = () => resolve(0);
            req.onupgradeneeded = () => { created = true; /* didn't exist */ };
            req.onsuccess = async () => {
                const db = req.result;
                try {
                    if (created || db.objectStoreNames.length === 0) {
                        db.close();
                        try { indexedDB.deleteDatabase(LEGACY_DB_NAME); } catch (_) {}
                        return resolve(0);
                    }
                    const storeNames = [STORES.COMPONENTS, STORES.VIEW_META, STORES.DOM_INSTRUCTIONS];
                    let migrated = 0;
                    for (const sn of storeNames) {
                        if (!db.objectStoreNames.contains(sn)) continue;
                        const recs = await new Promise((res2) => {
                            try {
                                const tx = db.transaction(sn, 'readonly');
                                const r = tx.objectStore(sn).getAll();
                                r.onsuccess = () => res2(r.result || []);
                                r.onerror = () => res2([]);
                            } catch (_) { res2([]); }
                        });
                        if (recs.length === 0) continue;
                        const map = await _loadStoreBlob(sn);
                        for (const raw of recs) {
                            try {
                                const inner = (raw && raw.data) ? raw.data : raw;
                                const rec = await decrypt(inner);
                                if (rec && rec.id) { map.set(rec.id, rec); migrated++; }
                            } catch (_) { /* skip corrupt */ }
                        }
                        await _saveStoreBlob(sn);
                    }
                    db.close();
                    try { indexedDB.deleteDatabase(LEGACY_DB_NAME); } catch (_) {}
                    if (migrated > 0) logSys('INFO', 'Migrated ' + migrated + ' legacy IndexedDB records to OPFS');
                    resolve(migrated);
                } catch (e) {
                    try { db.close(); } catch (_) {}
                    resolve(0);
                }
            };
        });
    }

    // ── Initialization ──

    async function _doInit(options) {
        try {
            await initCrypto();
            _opfs = await openOpfs();
            await _migrateLegacyIndexedDb();

            // Restore saved theme preference
            try {
                const saved = localStorage.getItem('cepha-theme');
                if (saved === 'dark') _activeTheme = 'dark';
            } catch (e) { /* localStorage unavailable */ }

            // Load cache from OPFS sealed blobs
            await loadCache();

            // Seed defaults if empty
            if (_cache.components.size === 0) {
                await seedDefaults();
            }

            _initialized = true;
            const encStatus = _keyPersisted ? 'persistent' : 'session-only';
            emit('init', { theme: _activeTheme, componentCount: _cache.components.size, encrypted: encStatus });
            console.log('%c🎨 CephaMaterialDb: Ready (' + _cache.components.size + ' components, theme: ' + _activeTheme + ', encrypted: ' + encStatus + ')', 'color:#667eea;font-weight:bold');
        } catch (e) {
            console.error('CephaMaterialDb init failed:', e);
        }
    }

    function init(options = {}) {
        if (_initialized) return Promise.resolve();
        if (!_initPromise) _initPromise = _doInit(options);
        return _initPromise;
    }

    async function loadCache() {
        if (!_opfs) return;
        const stores = [STORES.COMPONENTS, STORES.VIEW_META, STORES.DOM_INSTRUCTIONS];
        const cacheKeys = ['components', 'viewMeta', 'domInstructions'];

        for (let i = 0; i < stores.length; i++) {
            const records = await readAll(stores[i]);
            const map = _cache[cacheKeys[i]];
            map.clear();
            const plaintextRecords = [];
            for (const rec of records) {
                try {
                    const raw = rec.data || rec;
                    const wasEncrypted = raw && raw._encrypted;
                    const decrypted = await decrypt(raw);
                    if (decrypted && decrypted.id) {
                        map.set(decrypted.id, decrypted);
                        if (!wasEncrypted && _keyPersisted) plaintextRecords.push(decrypted);
                    }
                } catch (e) { /* skip corrupt/unreadable entries */ }
            }
            // Migrate legacy plaintext records to encrypted storage
            if (plaintextRecords.length > 0) {
                for (const rec of plaintextRecords) await putRecord(stores[i], rec);
                logSys('INFO', 'Encrypted ' + plaintextRecords.length + ' legacy plaintext records in "' + stores[i] + '"');
            }
        }
    }

    async function readAll(storeName) {
        if (!_opfs) return [];
        const map = await _loadStoreBlob(storeName);
        // Return envelope compatible with the old IDB shape used by loadCache:
        // loadCache expects `rec.data || rec` plus optional `_encrypted` flag.
        // We give it plain records (already decrypted at blob level).
        return Array.from(map.values());
    }

    // ── Default Component Seed ──
    // Seeds the base design system components used by all Cepha apps.

    async function seedDefaults() {
        const now = new Date().toISOString();

        // Core components every Cepha app has
        const components = [
            { id: 'root', name: 'Application Root', type: 'root', status: 'active', parentId: null, route: '/', metadata: {}, createdAt: now, updatedAt: now },
            { id: 'nav', name: 'Navigation Bar', type: 'nav', status: 'active', parentId: 'root', route: null, metadata: { sticky: true, height: 64 }, createdAt: now, updatedAt: now },
            { id: 'drawer', name: 'Navigation Drawer', type: 'drawer', status: 'active', parentId: 'root', route: null, metadata: { width: 280, position: 'left' }, createdAt: now, updatedAt: now },
            { id: 'main', name: 'Main Content', type: 'container', status: 'active', parentId: 'root', route: null, metadata: { maxWidth: 1200 }, createdAt: now, updatedAt: now },
            { id: 'footer', name: 'Footer', type: 'footer', status: 'active', parentId: 'root', route: null, metadata: {}, createdAt: now, updatedAt: now },
            { id: 'security-badge', name: 'Security Badge', type: 'indicator', status: 'active', parentId: 'root', route: null, metadata: { position: 'fixed', bottom: 16, right: 16 }, createdAt: now, updatedAt: now },
            { id: 'snackbar', name: 'Snackbar', type: 'notification', status: 'active', parentId: 'root', route: null, metadata: { duration: 4000 }, createdAt: now, updatedAt: now },
            { id: 'loader', name: 'Page Loader', type: 'overlay', status: 'active', parentId: 'root', route: null, metadata: {}, createdAt: now, updatedAt: now },
            // Store components — registered for both local (7702) and production (workers.dev)
            { id: 'store-hero', name: 'Store Hero Jumbotron', type: 'section', status: 'active', parentId: 'main', route: '/Home/Index', metadata: { gradient: 'primary→secondary', height: 'auto', padding: 64 }, createdAt: now, updatedAt: now },
            { id: 'store-passkey-card', name: 'PassKey Status Card', type: 'card', status: 'active', parentId: 'main', route: '/PassKey/Index', metadata: { border: 'primary', padding: 32 }, createdAt: now, updatedAt: now },
            { id: 'store-feature-card', name: 'Feature Card', type: 'card', status: 'active', parentId: 'main', route: '/Catalog/Index', metadata: { borderLeft: 3, hoverTranslateY: -4 }, createdAt: now, updatedAt: now },
            { id: 'store-free-card', name: 'Free Template Card', type: 'card', status: 'active', parentId: 'main', route: '/Home/Index', metadata: { borderLeftColor: 'success' }, createdAt: now, updatedAt: now },
            { id: 'store-premium-card', name: 'Premium Feature Card', type: 'card', status: 'active', parentId: 'main', route: '/Catalog/Index', metadata: { borderLeftColor: 'gold' }, createdAt: now, updatedAt: now },
            { id: 'store-specs', name: 'Feature Specs Bar', type: 'inline', status: 'active', parentId: 'store-feature-card', route: null, metadata: { specs: ['payload', 'memory', 'cpu', 'tier'] }, createdAt: now, updatedAt: now },
            { id: 'store-cli-sync', name: 'CLI Sync Box', type: 'code-block', status: 'active', parentId: 'store-passkey-card', route: null, metadata: { fontFamily: 'mono', borderStyle: 'dashed' }, createdAt: now, updatedAt: now },
            { id: 'store-toast', name: 'Store Toast', type: 'notification', status: 'active', parentId: 'root', route: null, metadata: { position: 'fixed', bottom: 24, right: 24, duration: 3000 }, createdAt: now, updatedAt: now },
            { id: 'store-grid', name: 'Feature Grid', type: 'grid', status: 'active', parentId: 'main', route: null, metadata: { columns: 'auto-fill', minWidth: 340, gap: 20 }, createdAt: now, updatedAt: now },
            { id: 'store-badge', name: 'Mode Badge', type: 'badge', status: 'active', parentId: 'nav', route: null, metadata: { variants: ['server', 'local'] }, createdAt: now, updatedAt: now }
        ];

        for (const comp of components) {
            await putRecord(STORES.COMPONENTS, comp);
            _cache.components.set(comp.id, comp);
        }

        // Seed viewMeta for both light and dark themes
        for (const comp of components) {
            for (const themeId of ['light', 'dark']) {
                const tokens = DEFAULT_TOKENS[themeId];
                const meta = {
                    id: `${comp.id}__${themeId}`,
                    componentId: comp.id,
                    themeId,
                    position: { x: null, y: null },
                    size: { w: null, h: null },
                    colors: {
                        bg: tokens.bg,
                        border: tokens.border,
                        text: tokens.text,
                        accent: tokens.primary
                    },
                    icon: null,
                    font: {
                        family: tokens.fontFamily,
                        size: tokens.fontSize,
                        weight: tokens.fontWeight,
                        style: 'normal'
                    },
                    spacing: { margin: null, padding: null },
                    radius: null,
                    elevation: 1,
                    opacity: 1,
                    zIndex: comp.id === 'nav' ? 1000 : comp.id === 'security-badge' ? 9999 : null,
                    customCSS: null
                };
                await putRecord(STORES.VIEW_META, meta);
                _cache.viewMeta.set(meta.id, meta);
            }
        }

        // Seed domInstructions for each component
        for (const comp of components) {
            const instr = {
                id: `${comp.id}__dom`,
                componentId: comp.id,
                animation: { type: 'fadeIn', duration: '300ms', easing: 'ease-out', delay: '0ms' },
                hover: { effect: null, duration: '200ms' },
                click: { effect: null, duration: '150ms' },
                transition: { property: 'all', duration: '0.2s', easing: 'cubic-bezier(0.4,0,0.2,1)' },
                connections: [],
                responsive: {
                    breakpoints: { sm: 640, md: 768, lg: 1024, xl: 1280 }
                },
                integrity: { hash: null, selector: null, attributes: [] },
                customJS: null
            };
            await putRecord(STORES.DOM_INSTRUCTIONS, instr);
            _cache.domInstructions.set(instr.id, instr);
        }

        logSys('INFO', 'Design system seeded: ' + components.length + ' components, 2 themes');
    }

    // ── CRUD Operations ──

    async function putRecord(storeName, record) {
        if (!_opfs) return;
        const map = await _loadStoreBlob(storeName);
        map.set(record.id, record);
        await _saveStoreBlob(storeName);
    }

    async function deleteRecord(storeName, id) {
        if (!_opfs) return;
        const map = await _loadStoreBlob(storeName);
        if (map.delete(id)) await _saveStoreBlob(storeName);
    }

    // ── Component API ──

    async function registerComponent(spec) {
        const now = new Date().toISOString();
        const comp = {
            id: spec.id || generateId(),
            name: spec.name || 'Unnamed',
            type: spec.type || 'custom',
            status: spec.status || 'active',
            parentId: spec.parentId || null,
            route: spec.route || null,
            metadata: spec.metadata || {},
            createdAt: now,
            updatedAt: now
        };

        await putRecord(STORES.COMPONENTS, comp);
        _cache.components.set(comp.id, comp);

        // Create default viewMeta for active theme
        const defaultMeta = createDefaultViewMeta(comp.id, _activeTheme);
        await putRecord(STORES.VIEW_META, defaultMeta);
        _cache.viewMeta.set(defaultMeta.id, defaultMeta);

        // Create default domInstructions
        const defaultInstr = createDefaultDomInstructions(comp.id);
        await putRecord(STORES.DOM_INSTRUCTIONS, defaultInstr);
        _cache.domInstructions.set(defaultInstr.id, defaultInstr);

        emit('component:registered', comp);
        logSys('DEBUG', 'Component registered: ' + comp.name + ' (' + comp.id + ')');
        return comp;
    }

    async function updateComponent(id, updates) {
        const existing = _cache.components.get(id);
        if (!existing) return null;

        const updated = { ...existing, ...updates, id, updatedAt: new Date().toISOString() };
        await putRecord(STORES.COMPONENTS, updated);
        _cache.components.set(id, updated);
        emit('component:updated', updated);
        return updated;
    }

    async function removeComponent(id) {
        _cache.components.delete(id);
        await deleteRecord(STORES.COMPONENTS, id);

        // Remove associated viewMeta and domInstructions
        for (const [key, vm] of _cache.viewMeta) {
            if (vm.componentId === id) {
                _cache.viewMeta.delete(key);
                await deleteRecord(STORES.VIEW_META, key);
            }
        }
        for (const [key, di] of _cache.domInstructions) {
            if (di.componentId === id) {
                _cache.domInstructions.delete(key);
                await deleteRecord(STORES.DOM_INSTRUCTIONS, key);
            }
        }

        emit('component:removed', { id });
    }

    function getComponent(id) {
        return _cache.components.get(id) || null;
    }

    function queryComponents(filter = {}) {
        let results = [..._cache.components.values()];
        if (filter.type) results = results.filter(c => c.type === filter.type);
        if (filter.status) results = results.filter(c => c.status === filter.status);
        if (filter.parentId !== undefined) results = results.filter(c => c.parentId === filter.parentId);
        if (filter.route) results = results.filter(c => c.route === filter.route);
        return results;
    }

    // ── ViewMeta API ──

    async function setViewMeta(componentId, themeId, props) {
        const id = `${componentId}__${themeId}`;
        const existing = _cache.viewMeta.get(id);
        const meta = existing
            ? deepMerge(existing, { ...props, id, componentId, themeId })
            : { ...createDefaultViewMeta(componentId, themeId), ...props, id, componentId, themeId };

        await putRecord(STORES.VIEW_META, meta);
        _cache.viewMeta.set(id, meta);

        // If this is the active theme, regenerate CSS
        if (themeId === _activeTheme) {
            applyTokensToDOM(componentId);
        }

        emit('viewMeta:updated', meta);
        return meta;
    }

    function getViewMeta(componentId, themeId) {
        const id = `${componentId}__${themeId || _activeTheme}`;
        return _cache.viewMeta.get(id) || null;
    }

    function queryViewMeta(filter = {}) {
        let results = [..._cache.viewMeta.values()];
        if (filter.componentId) results = results.filter(v => v.componentId === filter.componentId);
        if (filter.themeId) results = results.filter(v => v.themeId === filter.themeId);
        return results;
    }

    // ── DomInstructions API ──

    async function setDomInstructions(componentId, props) {
        const id = `${componentId}__dom`;
        const existing = _cache.domInstructions.get(id);
        const instr = existing
            ? deepMerge(existing, { ...props, id, componentId })
            : { ...createDefaultDomInstructions(componentId), ...props, id, componentId };

        await putRecord(STORES.DOM_INSTRUCTIONS, instr);
        _cache.domInstructions.set(id, instr);
        emit('domInstructions:updated', instr);
        return instr;
    }

    function getDomInstructions(componentId) {
        return _cache.domInstructions.get(`${componentId}__dom`) || null;
    }

    // ── Integrity Snapshot (CephaSecurity Integration) ──
    // Stores a SHA-256 hash of the rendered DOM for a component,
    // enabling CephaSecurity to verify DOM hasn't been tampered with.

    async function storeIntegritySnapshot(componentId, selector) {
        const el = document.querySelector(selector);
        if (!el) return null;

        const hash = await computeHash(el.innerHTML);
        const attributes = [];
        for (const attr of el.attributes) {
            if (attr.name !== 'style') {
                attributes.push({ name: attr.name, value: attr.value });
            }
        }

        const instr = getDomInstructions(componentId);
        if (instr) {
            instr.integrity = { hash, selector, attributes, timestamp: new Date().toISOString() };
            await putRecord(STORES.DOM_INSTRUCTIONS, instr);
            _cache.domInstructions.set(instr.id, instr);
            emit('integrity:stored', { componentId, hash });
        }
        return hash;
    }

    async function verifyIntegrity(componentId) {
        const instr = getDomInstructions(componentId);
        if (!instr || !instr.integrity || !instr.integrity.hash) return { valid: true, reason: 'no_snapshot' };

        const el = document.querySelector(instr.integrity.selector);
        if (!el) return { valid: false, reason: 'element_missing', selector: instr.integrity.selector };

        const currentHash = await computeHash(el.innerHTML);
        if (currentHash !== instr.integrity.hash) {
            return { valid: false, reason: 'hash_mismatch', expected: instr.integrity.hash, actual: currentHash };
        }

        // Verify attributes
        for (const attr of (instr.integrity.attributes || [])) {
            if (el.getAttribute(attr.name) !== attr.value) {
                return { valid: false, reason: 'attribute_mismatch', attribute: attr.name, expected: attr.value, actual: el.getAttribute(attr.name) };
            }
        }

        return { valid: true };
    }

    async function verifyAll() {
        const results = {};
        for (const [, instr] of _cache.domInstructions) {
            if (instr.integrity && instr.integrity.hash) {
                results[instr.componentId] = await verifyIntegrity(instr.componentId);
            }
        }
        return results;
    }

    // ── Theme System ──

    function getActiveTheme() {
        return _activeTheme;
    }

    async function setTheme(themeId) {
        if (!DEFAULT_TOKENS[themeId] && !hasCustomTheme(themeId)) {
            console.warn('CephaMaterialDb: Unknown theme "' + themeId + '"');
            return;
        }

        _activeTheme = themeId;
        try { localStorage.setItem('cepha-theme', themeId === 'light' ? '' : themeId); } catch (e) {}

        // Apply theme attribute (CSS handles default light/dark via [data-theme])
        document.documentElement.setAttribute('data-theme', themeId === 'light' ? '' : themeId);

        // Only apply inline tokens for custom themes; default themes use CSS rules
        if (hasCustomTheme(themeId)) {
            applyAllTokens();
        } else {
            // Clear inline tokens so CSS rules take effect
            document.documentElement.style.cssText = '';
        }

        emit('theme:changed', { theme: themeId });
        for (const fn of _themeListeners) {
            try { fn(themeId); } catch (e) {}
        }
    }

    // Sync internal state without applying inline tokens (CSS handles default themes)
    function syncThemeState(themeId) {
        _activeTheme = themeId;
        try { localStorage.setItem('cepha-theme', themeId === 'light' ? '' : themeId); } catch (e) {}
    }

    function hasCustomTheme(themeId) {
        for (const [, vm] of _cache.viewMeta) {
            if (vm.themeId === themeId) return true;
        }
        return false;
    }

    async function createTheme(themeId, baseTheme, overrides = {}) {
        const base = DEFAULT_TOKENS[baseTheme] || DEFAULT_TOKENS.light;
        const merged = { ...base, ...overrides };

        // Store as viewMeta for the root component
        await setViewMeta('root', themeId, {
            colors: {
                bg: merged.bg,
                border: merged.border,
                text: merged.text,
                accent: merged.primary
            }
        });

        // Store full token set as metadata on the root component
        const root = getComponent('root');
        if (root) {
            const themes = root.metadata.customThemes || {};
            themes[themeId] = merged;
            await updateComponent('root', { metadata: { ...root.metadata, customThemes: themes } });
        }

        emit('theme:created', { themeId, baseTheme });
        return merged;
    }

    function getThemeTokens(themeId) {
        const tid = themeId || _activeTheme;
        if (DEFAULT_TOKENS[tid]) return { ...DEFAULT_TOKENS[tid] };

        // Custom theme stored in root component metadata
        const root = getComponent('root');
        if (root && root.metadata.customThemes && root.metadata.customThemes[tid]) {
            return { ...root.metadata.customThemes[tid] };
        }
        return { ...DEFAULT_TOKENS.light };
    }

    function listThemes() {
        const themes = ['light', 'dark'];
        const root = getComponent('root');
        if (root && root.metadata.customThemes) {
            themes.push(...Object.keys(root.metadata.customThemes));
        }
        return themes;
    }

    // ── CSS Generation ──
    // Applies design tokens as CSS custom properties on :root

    function applyAllTokens() {
        const tokens = getThemeTokens(_activeTheme);
        const root = document.documentElement;
        for (const [key, cssVar] of Object.entries(TOKEN_CSS_MAP)) {
            if (tokens[key] !== undefined) {
                root.style.setProperty(cssVar, tokens[key]);
            }
        }
    }

    function applyTokensToDOM(componentId) {
        const meta = getViewMeta(componentId, _activeTheme);
        if (!meta) return;

        // Apply component-specific overrides as scoped CSS
        const comp = getComponent(componentId);
        if (!comp) return;

        // Generate scoped CSS for this component if it has custom colors
        if (meta.customCSS) {
            let styleEl = document.getElementById(`cepha-style-${componentId}`);
            if (!styleEl) {
                styleEl = document.createElement('style');
                styleEl.id = `cepha-style-${componentId}`;
                styleEl.setAttribute('data-cepha-materialdb', componentId);
                document.head.appendChild(styleEl);
            }
            styleEl.textContent = meta.customCSS;
        }
    }

    function generateCSS(themeId) {
        const tokens = getThemeTokens(themeId || _activeTheme);
        let css = `/* CephaMaterialDb — Generated Theme: ${themeId || _activeTheme} */\n:root {\n`;
        for (const [key, cssVar] of Object.entries(TOKEN_CSS_MAP)) {
            if (tokens[key] !== undefined) {
                css += `  ${cssVar}: ${tokens[key]};\n`;
            }
        }
        css += '}\n';

        // Component-specific overrides
        for (const [, meta] of _cache.viewMeta) {
            if (meta.themeId === (themeId || _activeTheme) && meta.customCSS) {
                css += `\n/* Component: ${meta.componentId} */\n${meta.customCSS}\n`;
            }
        }

        return css;
    }

    // ── Export / Import ──

    function exportDesignSystem() {
        return {
            version: 1,
            exportedAt: new Date().toISOString(),
            activeTheme: _activeTheme,
            components: [..._cache.components.values()],
            viewMeta: [..._cache.viewMeta.values()],
            domInstructions: [..._cache.domInstructions.values()]
        };
    }

    async function importDesignSystem(data) {
        if (!data || data.version !== 1) throw new Error('Invalid design system export');

        // Clear existing
        _cache.components.clear();
        _cache.viewMeta.clear();
        _cache.domInstructions.clear();

        for (const comp of (data.components || [])) {
            await putRecord(STORES.COMPONENTS, comp);
            _cache.components.set(comp.id, comp);
        }
        for (const vm of (data.viewMeta || [])) {
            await putRecord(STORES.VIEW_META, vm);
            _cache.viewMeta.set(vm.id, vm);
        }
        for (const di of (data.domInstructions || [])) {
            await putRecord(STORES.DOM_INSTRUCTIONS, di);
            _cache.domInstructions.set(di.id, di);
        }

        if (data.activeTheme) await setTheme(data.activeTheme);
        emit('designSystem:imported', { components: _cache.components.size });
    }

    // ── Query API (unified) ──

    function query(options = {}) {
        const store = options.store || 'components';
        const cacheMap = _cache[store];
        if (!cacheMap) return [];

        let results = [...cacheMap.values()];
        if (options.filter) {
            for (const [key, val] of Object.entries(options.filter)) {
                results = results.filter(r => {
                    const v = r[key];
                    return typeof v === 'object' ? JSON.stringify(v).includes(val) : v === val;
                });
            }
        }
        if (options.limit) results = results.slice(0, options.limit);
        return results;
    }

    // ── Statistics ──

    function getStats() {
        return {
            components: _cache.components.size,
            viewMeta: _cache.viewMeta.size,
            domInstructions: _cache.domInstructions.size,
            activeTheme: _activeTheme,
            themes: listThemes(),
            integritySnapshots: [..._cache.domInstructions.values()].filter(d => d.integrity && d.integrity.hash).length
        };
    }

    // ── Event Emitter ──

    function on(event, callback) {
        if (event === 'theme') {
            _themeListeners.add(callback);
            return () => _themeListeners.delete(callback);
        }
        if (!_listeners.has(event)) _listeners.set(event, new Set());
        _listeners.get(event).add(callback);
        return () => off(event, callback);
    }

    function off(event, callback) {
        _listeners.get(event)?.delete(callback);
    }

    function emit(event, data) {
        const handlers = _listeners.get(event);
        if (!handlers) return;
        for (const fn of handlers) {
            try { fn(data); } catch (e) { console.error('[CephaMaterialDb] Event error:', e); }
        }
        // Also emit wildcard
        const wild = _listeners.get('*');
        if (wild) {
            for (const fn of wild) {
                try { fn({ event, data }); } catch (e) {}
            }
        }
    }

    // ── Helpers ──

    function generateId() {
        const arr = crypto.getRandomValues(new Uint8Array(6));
        return 'cm-' + Date.now().toString(36) + '-' + Array.from(arr, b => b.toString(16).padStart(2,'0')).join('').substring(0, 8);
    }

    async function computeHash(text) {
        const data = new TextEncoder().encode(text);
        const hash = await crypto.subtle.digest('SHA-256', data);
        return Array.from(new Uint8Array(hash).slice(0, 8))
            .map(b => b.toString(16).padStart(2, '0')).join('');
    }

    function deepMerge(target, source) {
        const result = { ...target };
        for (const key of Object.keys(source)) {
            if (key === '__proto__' || key === 'constructor' || key === 'prototype') continue;
            if (source[key] && typeof source[key] === 'object' && !Array.isArray(source[key]) && !(source[key] instanceof Uint8Array)) {
                result[key] = deepMerge(result[key] || {}, source[key]);
            } else {
                result[key] = source[key];
            }
        }
        return result;
    }

    function createDefaultViewMeta(componentId, themeId) {
        const tokens = DEFAULT_TOKENS[themeId] || DEFAULT_TOKENS.light;
        return {
            id: `${componentId}__${themeId}`,
            componentId,
            themeId,
            position: { x: null, y: null },
            size: { w: null, h: null },
            colors: { bg: tokens.bg, border: tokens.border, text: tokens.text, accent: tokens.primary },
            icon: null,
            font: { family: tokens.fontFamily, size: tokens.fontSize, weight: tokens.fontWeight, style: 'normal' },
            spacing: { margin: null, padding: null },
            radius: null,
            elevation: 1,
            opacity: 1,
            zIndex: null,
            customCSS: null
        };
    }

    function createDefaultDomInstructions(componentId) {
        return {
            id: `${componentId}__dom`,
            componentId,
            animation: { type: 'none', duration: '300ms', easing: 'ease-out', delay: '0ms' },
            hover: { effect: null, duration: '200ms' },
            click: { effect: null, duration: '150ms' },
            transition: { property: 'all', duration: '0.2s', easing: 'cubic-bezier(0.4,0,0.2,1)' },
            connections: [],
            responsive: { breakpoints: { sm: 640, md: 768, lg: 1024, xl: 1280 } },
            integrity: { hash: null, selector: null, attributes: [] },
            customJS: null
        };
    }

    function logSys(level, message) {
        if (typeof CephaSysLog !== 'undefined') {
            CephaSysLog.log(level, 'SYS', message, { source: 'CephaMaterialDb' });
        }
    }

    function isInitialized() { return _initialized; }

    // ── Public API ──

    return {
        init,
        isInitialized,

        // Component CRUD
        registerComponent,
        updateComponent,
        removeComponent,
        getComponent,
        queryComponents,

        // ViewMeta
        setViewMeta,
        getViewMeta,
        queryViewMeta,

        // DomInstructions
        setDomInstructions,
        getDomInstructions,

        // Integrity (CephaSecurity bridge)
        storeIntegritySnapshot,
        verifyIntegrity,
        verifyAll,

        // Theme system
        getActiveTheme,
        setTheme,
        syncThemeState,
        createTheme,
        getThemeTokens,
        listThemes,

        // CSS generation
        applyAllTokens,
        generateCSS,

        // Export / Import
        exportDesignSystem,
        importDesignSystem,

        // Query
        query,
        getStats,

        // Events
        on,
        off,

        // Constants
        DEFAULT_TOKENS,
        TOKEN_CSS_MAP,
        STORES
    };
})();
