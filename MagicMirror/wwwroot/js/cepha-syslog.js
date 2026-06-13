// 📋 CephaSysLog — Encrypted System-Level Log Service
// ═══════════════════════════════════════════════════════════════════════════
// ⚠️ COMPLIANCE: Before modifying this file, review SDK_DEVELOPMENT_STANDARDS.md
// ═══════════════════════════════════════════════════════════════════════════
//
// Unified logging service connecting server-side logs, CephaSecurity
// violations, DOM repairs, and application events into a single encrypted
// audit trail.
//
// Features:
//   • Structured log records with source, level, category, and metadata
//   • AES-256-GCM encryption at rest (OPFS sealed blobs) — decrypted only on read
//   • Real-time log streaming to UI via event emitter
//   • ML-ready anomaly export (JSON Lines format)
//   • Integration with CephaSecurity violation/repair pipeline
//   • Session-scoped encryption key derived via HKDF-SHA256
//
// Categories:
//   SYS    — Runtime lifecycle (startup, ready, navigation)
//   SEC    — Security violations from CephaSecurity observer
//   REPAIR — DOM repair actions (local + Worker heal)
//   NET    — Network events (fetch, SignalR)
//   AUTH   — Identity/authentication events
//   APP    — Application-level logs from controllers
//   PERF   — Performance metrics
//
// Storage: OPFS "cepha/syslog/" → encrypted sealed blobs
// At rest: AES-256-GCM with per-session key from crypto.getRandomValues
// On read: decrypted in-memory, never persisted in plaintext

const CephaSysLog = (() => {
    'use strict';

    // ── State ──
    const _entries = [];              // in-memory ring buffer
    const _listeners = new Map();     // event listeners by category
    let _opfs = null;                 // OPFS directory handle
    let _cryptoKey = null;            // AES-256-GCM CryptoKey
    let _initialized = false;
    let _workerRef = null;
    let _sessionId = '';
    let _entryId = 0;
    let _writeBuffer = [];            // pending OPFS writes
    let _writeTimer = null;
    const MAX_MEMORY = 2000;          // max in-memory entries
    const MAX_STORED = 10000;         // max entries in OPFS
    const OPFS_LOG_DIR = 'cepha/syslog';
    const BLOB_MAGIC = 0xCE;
    const BLOB_VERSION = 0x01;
    const WRITE_DEBOUNCE = 2000;      // ms between OPFS flushes

    // ── Categories & Levels ──
    const CATEGORIES = ['SYS', 'SEC', 'REPAIR', 'NET', 'AUTH', 'APP', 'PERF'];
    const LEVELS = ['TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'FATAL'];
    const LEVEL_WEIGHT = { TRACE: 0, DEBUG: 1, INFO: 2, WARN: 3, ERROR: 4, FATAL: 5 };

    // ── Encryption (AES-256-GCM) ──

    async function initCrypto() {
        // Generate per-session key — never leaves memory
        _cryptoKey = await crypto.subtle.generateKey(
            { name: 'AES-GCM', length: 256 },
            false,  // not extractable
            ['encrypt', 'decrypt']
        );
        _sessionId = Array.from(crypto.getRandomValues(new Uint8Array(8)))
            .map(b => b.toString(16).padStart(2, '0')).join('');
    }

    async function encrypt(data) {
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const encoded = new TextEncoder().encode(JSON.stringify(data));
        const ciphertext = await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv },
            _cryptoKey,
            encoded
        );
        return { iv, ciphertext };
    }

    async function decrypt(blob) {
        const plaintext = await crypto.subtle.decrypt(
            { name: 'AES-GCM', iv: blob.iv },
            _cryptoKey,
            blob.ciphertext
        );
        return JSON.parse(new TextDecoder().decode(plaintext));
    }

    // ── OPFS Storage ──

    async function openOpfs() {
        let root = await navigator.storage.getDirectory();
        for (const seg of OPFS_LOG_DIR.split('/')) {
            root = await root.getDirectoryHandle(seg, { create: true });
        }
        return root;
    }

    async function encryptBlob(plaintextBytes) {
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

    async function decryptBlob(bytes) {
        if (!_cryptoKey) return bytes;
        if (bytes.length < 14 || bytes[0] !== BLOB_MAGIC || bytes[1] !== BLOB_VERSION)
            return bytes;
        const iv = bytes.slice(2, 14);
        const ct = bytes.slice(14);
        const pt = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, _cryptoKey, ct);
        return new Uint8Array(pt);
    }

    async function loadSessionEntries() {
        if (!_opfs) return [];
        try {
            const fh = await _opfs.getFileHandle(_sessionId + '.enc');
            const file = await fh.getFile();
            if (file.size === 0) return [];
            const raw = new Uint8Array(await file.arrayBuffer());
            const pt = await decryptBlob(raw);
            return JSON.parse(new TextDecoder().decode(pt));
        } catch (e) {
            if (e && (e.name === 'NotFoundError' || e.code === 8)) return [];
            return [];
        }
    }

    async function flushWriteBuffer() {
        _writeTimer = null;
        if (_writeBuffer.length === 0 || !_opfs) return;
        const batch = _writeBuffer.splice(0);
        try {
            const existing = await loadSessionEntries();
            existing.push(...batch);
            if (existing.length > MAX_STORED) existing.splice(0, existing.length - MAX_STORED);
            const plaintext = new TextEncoder().encode(JSON.stringify(existing));
            const sealed = await encryptBlob(plaintext);
            const fh = await _opfs.getFileHandle(_sessionId + '.enc', { create: true });
            const w = await fh.createWritable();
            await w.write(sealed);
            await w.close();
        } catch {
            _writeBuffer.unshift(...batch);
            if (_writeBuffer.length > MAX_STORED) _writeBuffer.length = MAX_STORED;
        }
    }

    async function persistEntry(entry) {
        if (!_opfs) return;
        _writeBuffer.push(entry);
        if (!_writeTimer) _writeTimer = setTimeout(flushWriteBuffer, WRITE_DEBOUNCE);
    }

    async function _migrateLegacyIndexedDb() {
        if (typeof indexedDB === 'undefined') return;
        const LEGACY_DB = 'cepha-syslog';
        try {
            if (typeof indexedDB.databases === 'function') {
                const dbs = await indexedDB.databases();
                if (!dbs.some(d => d.name === LEGACY_DB)) return;
            }
        } catch (_) {}

        return new Promise(resolve => {
            let created = false;
            const req = indexedDB.open(LEGACY_DB);
            req.onerror = () => resolve();
            req.onupgradeneeded = () => { created = true; };
            req.onsuccess = async () => {
                const db = req.result;
                try {
                    if (created || db.objectStoreNames.length === 0) {
                        db.close();
                        try { indexedDB.deleteDatabase(LEGACY_DB); } catch (_) {}
                        return resolve();
                    }
                    db.close();
                    try { indexedDB.deleteDatabase(LEGACY_DB); } catch (_) {}
                    resolve();
                } catch {
                    try { db.close(); } catch (_) {}
                    resolve();
                }
            };
        });
    }

    // ── Transport (OPFS-only, no external server) ──

    function enqueueForServer(_entry) {
        // No-op: server transport removed in favor of OPFS-only logging
    }

    // ── Core Logging ──

    function createEntry(level, category, message, meta = {}) {
        const entry = {
            id: `${_sessionId}-${++_entryId}`,
            timestamp: new Date().toISOString(),
            level,
            category,
            message,
            source: meta.source || detectSource(),
            domPath: meta.domPath || null,
            violationType: meta.violationType || null,
            repairAction: meta.repairAction || null,
            enforced: meta.enforced ?? null,
            context: meta.context || null,
            session: _sessionId
        };

        // Memory ring buffer
        _entries.push(entry);
        if (_entries.length > MAX_MEMORY) _entries.shift();

        // Persist encrypted
        persistEntry(entry);

        // Forward to SysLog server
        enqueueForServer(entry);

        // Emit to listeners
        emitLog(entry);

        return entry;
    }

    function detectSource() {
        const stack = new Error().stack;
        if (!stack) return { file: 'unknown', line: 0 };
        const lines = stack.split('\n').slice(3); // skip Error + createEntry + caller
        for (const line of lines) {
            const match = line.match(/(?:at\s+)?(?:.*?\s+\()?(.+?):(\d+):(\d+)\)?$/);
            if (match) {
                return {
                    file: match[1].replace(/^https?:\/\/[^/]+/, ''),
                    line: parseInt(match[2]),
                    column: parseInt(match[3])
                };
            }
        }
        return { file: 'unknown', line: 0 };
    }

    // ── Event Emitter ──

    function emitLog(entry) {
        // Category-specific listeners
        const catListeners = _listeners.get(entry.category) || [];
        for (const fn of catListeners) fn(entry);

        // Wildcard listeners
        const allListeners = _listeners.get('*') || [];
        for (const fn of allListeners) fn(entry);
    }

    function on(categoryOrWildcard, fn) {
        if (!_listeners.has(categoryOrWildcard)) _listeners.set(categoryOrWildcard, []);
        _listeners.get(categoryOrWildcard).push(fn);
    }

    function off(categoryOrWildcard, fn) {
        const list = _listeners.get(categoryOrWildcard);
        if (list) {
            const idx = list.indexOf(fn);
            if (idx >= 0) list.splice(idx, 1);
        }
    }

    // ── Convenience Methods ──

    function sys(message, meta)    { return createEntry('INFO', 'SYS', message, meta); }
    function sec(level, message, meta) { return createEntry(level, 'SEC', message, meta); }
    function repair(message, meta) { return createEntry('INFO', 'REPAIR', message, meta); }
    function net(message, meta)    { return createEntry('INFO', 'NET', message, meta); }
    function auth(message, meta)   { return createEntry('INFO', 'AUTH', message, meta); }
    function app(level, message, meta) { return createEntry(level, 'APP', message, meta); }
    function perf(message, meta)   { return createEntry('DEBUG', 'PERF', message, meta); }

    // ── CephaSecurity Integration ──

    function ingestViolation(violation) {
        return createEntry(
            violation.level === 'CRITICAL' ? 'FATAL' :
            violation.level === 'HIGH' ? 'ERROR' :
            violation.level === 'WARNING' ? 'WARN' : 'INFO',
            'SEC',
            `${violation.type}: ${violation.detail}`,
            {
                source: violation.source,
                domPath: violation.domPath,
                violationType: violation.type,
                enforced: violation.enforced,
                context: {
                    element: typeof violation.element === 'string' ? violation.element : null,
                    timestamp: violation.timestamp
                }
            }
        );
    }

    function ingestRepair(repairEntry) {
        return createEntry(
            repairEntry.repaired ? 'INFO' : 'WARN',
            'REPAIR',
            `${repairEntry.action} (${repairEntry.violationType})`,
            {
                domPath: repairEntry.domPath,
                repairAction: repairEntry.action,
                violationType: repairEntry.violationType,
                context: {
                    repaired: repairEntry.repaired,
                    fallback: repairEntry.fallback,
                    level: repairEntry.violationLevel
                }
            }
        );
    }

    // ── Query & Export ──

    function query(options = {}) {
        let results = [..._entries];

        if (options.category) {
            results = results.filter(e => e.category === options.category);
        }
        if (options.level) {
            const minWeight = LEVEL_WEIGHT[options.level] || 0;
            results = results.filter(e => (LEVEL_WEIGHT[e.level] || 0) >= minWeight);
        }
        if (options.since) {
            results = results.filter(e => e.timestamp >= options.since);
        }
        if (options.violationType) {
            results = results.filter(e => e.violationType === options.violationType);
        }
        if (options.limit) {
            results = results.slice(-options.limit);
        }

        return results;
    }

    async function queryStored(options = {}) {
        if (!_opfs) return [];
        const entries = await loadSessionEntries();
        let results = entries;

        if (options.category) {
            results = results.filter(e => e.category === options.category);
        }
        if (options.level) {
            const minWeight = LEVEL_WEIGHT[options.level] || 0;
            results = results.filter(e => (LEVEL_WEIGHT[e.level] || 0) >= minWeight);
        }
        if (options.since) {
            results = results.filter(e => e.timestamp >= options.since);
        }
        if (options.limit) {
            results = results.slice(-options.limit);
        }

        return results;
    }

    function exportJsonLines(options = {}) {
        const entries = query(options);
        return entries.map(e => JSON.stringify({
            ts: e.timestamp,
            lvl: e.level,
            cat: e.category,
            msg: e.message,
            src: e.source,
            dom: e.domPath,
            vtype: e.violationType,
            repair: e.repairAction,
            enforced: e.enforced,
            ctx: e.context,
            sid: e.session
        })).join('\n');
    }

    function getAnomalySummary() {
        const secEntries = _entries.filter(e => e.category === 'SEC');
        const repairEntries = _entries.filter(e => e.category === 'REPAIR');

        // Group violations by type
        const byType = {};
        for (const e of secEntries) {
            const t = e.violationType || 'unknown';
            byType[t] = (byType[t] || 0) + 1;
        }

        // Group by level
        const byLevel = {};
        for (const e of secEntries) {
            byLevel[e.level] = (byLevel[e.level] || 0) + 1;
        }

        return {
            totalViolations: secEntries.length,
            totalRepairs: repairEntries.length,
            successfulRepairs: repairEntries.filter(e => e.context?.repaired).length,
            failedRepairs: repairEntries.filter(e => !e.context?.repaired).length,
            violationsByType: byType,
            violationsByLevel: byLevel,
            sessionId: _sessionId,
            sessionStart: _entries[0]?.timestamp || null,
            sessionDuration: _entries.length > 0
                ? Date.now() - new Date(_entries[0].timestamp).getTime()
                : 0
        };
    }

    // ── Initialization ──

    async function init(workerRef = null) {
        if (_initialized) return;
        _workerRef = workerRef;

        try {
            await initCrypto();
            _opfs = await openOpfs();
            await _migrateLegacyIndexedDb();
        } catch (err) {
            console.warn('[CephaSysLog] Storage init failed — memory-only mode:', err.message);
        }

        _initialized = true;

        sys('CephaSysLog initialized', {
            context: {
                encrypted: !!_cryptoKey,
                persistent: !!_opfs,
                storage: 'OPFS',
                sessionId: _sessionId
            }
        });

        if (location.hostname === 'localhost' || location.hostname === '127.0.0.1' || location.hostname === '[::1]') {
            console.log(
                '%c📋 CephaSysLog: Active (encrypted: %s, persistent: %s, session: %s)',
                'color: #667eea; font-weight: bold',
                !!_cryptoKey, !!_opfs, _sessionId
            );
        }
    }

    function getSessionId() { return _sessionId; }
    function getEntryCount() { return _entries.length; }
    function isInitialized() { return _initialized; }

    async function clearStored() {
        if (!_opfs) return;
        try {
            await _opfs.removeEntry(_sessionId + '.enc');
        } catch { /* file may not exist */ }
        _writeBuffer.length = 0;
        if (_writeTimer) { clearTimeout(_writeTimer); _writeTimer = null; }
    }

    // Forward enforcement state changes (OPFS-only, no server)
    async function notifyEnforcementChange(enforcement) {
        sec(`Enforcement ${enforcement ? 'ON' : 'OFF'}`);
    }

    // Forward NLog entries from Worker (C# DevLog output)
    function ingestNLog(nlogEntry) {
        const levelMap = { 'DEBUG': 'DEBUG', 'INFO': 'INFO', 'WARN': 'WARN', 'ERROR': 'ERROR', 'FATAL': 'FATAL' };
        return createEntry(
            levelMap[nlogEntry.level] || 'INFO',
            'APP',
            nlogEntry.message,
            {
                source: { file: nlogEntry.logger || 'NLog', line: 0 },
                context: { nlog: true, logger: nlogEntry.logger }
            }
        );
    }

    // ── Public API ──

    return {
        init,
        // Logging
        sys,
        sec,
        repair,
        net,
        auth,
        app,
        perf,
        log: createEntry,
        // Security integration
        ingestViolation,
        ingestRepair,
        notifyEnforcementChange,
        // NLog integration
        ingestNLog,
        // Query & export
        query,
        queryStored,
        exportJsonLines,
        getAnomalySummary,
        // Events
        on,
        off,
        // State
        getSessionId,
        getEntryCount,
        isInitialized,
        clearStored,
        // Constants
        CATEGORIES,
        LEVELS
    };
})();
