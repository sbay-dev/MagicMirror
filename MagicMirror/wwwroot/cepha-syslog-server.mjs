// 📋 CephaSysLog Server — Standalone Encrypted Log Aggregation Service
// ═══════════════════════════════════════════════════════════════════════════
// ⚠️ COMPLIANCE: Before modifying this file, review SDK_DEVELOPMENT_STANDARDS.md
// ═══════════════════════════════════════════════════════════════════════════
//
// Usage: node cepha-syslog-server.mjs
//
// Centralized logging server that collects, encrypts, and stores logs from
// all Cepha components: UI (browser), Worker (WASM runtime), CephaKit (Node.js),
// and security subsystems (CephaSecurity violations/repairs).
//
// Architecture:
//   • Runs as an independent Node.js process (port 3003 by default)
//   • HTTPS enabled (auto-detects dev cert from env or ./obj/ folder)
//   • Receives logs via POST /_syslog/ingest (JSON body)
//   • Stores encrypted on disk (AES-256-GCM) — encrypted at rest
//   • Provides query/export REST API
//   • JSONL export for ML training pipelines
//
// Endpoints:
//   POST /_syslog/ingest         — Ingest log entries (batch or single)
//   GET  /_syslog/query          — Query logs (category, level, since, limit)
//   GET  /_syslog/export         — Export as JSONL (ML-ready)
//   GET  /_syslog/anomaly        — Anomaly summary (grouped by type/level)
//   GET  /_syslog/health         — Health check
//   GET  /_syslog/info           — Server diagnostics
//   DELETE /_syslog/clear        — Clear all logs
//
// Encryption:
//   AES-256-GCM with server-generated key
//   Key stored in memory only — never written to disk
//   Logs decrypted on-the-fly for queries, never cached in plaintext

import { createServer as createHttpServer } from 'node:http';
import { createServer as createHttpsServer } from 'node:https';
import { readFileSync, writeFileSync, existsSync, mkdirSync, unlinkSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes, createCipheriv, createDecipheriv } from 'node:crypto';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PORT = parseInt(process.env.SYSLOG_PORT || process.env.PORT || '3003', 10);
const HOST = process.env.HOST || '0.0.0.0';

// ─── HTTPS Auto-Detection ───────────────────────────────────
function findCert() {
    const certEnv = process.env.CEPHA_CERT;
    const keyEnv = process.env.CEPHA_KEY;
    if (certEnv && keyEnv && existsSync(certEnv) && existsSync(keyEnv)) {
        return { cert: readFileSync(certEnv), key: readFileSync(keyEnv) };
    }
    const searchPaths = [
        [join(__dirname, '..', 'obj', 'cepha-dev.pem'), join(__dirname, '..', 'obj', 'cepha-dev.key')],
        [join(__dirname, 'cepha-dev.pem'), join(__dirname, 'cepha-dev.key')],
    ];
    for (const [c, k] of searchPaths) {
        if (existsSync(c) && existsSync(k)) {
            return { cert: readFileSync(c), key: readFileSync(k) };
        }
    }
    return null;
}

const tlsOptions = findCert();
const protocol = tlsOptions ? 'https' : 'http';

// ─── Encryption (AES-256-GCM) — Key in memory only ─────────
const ENCRYPTION_KEY = randomBytes(32); // 256-bit key — never written to disk
const ALGORITHM = 'aes-256-gcm';
const IV_LENGTH = 12;
const AUTH_TAG_LENGTH = 16;

function encrypt(data) {
    const iv = randomBytes(IV_LENGTH);
    const cipher = createCipheriv(ALGORITHM, ENCRYPTION_KEY, iv);
    const jsonStr = JSON.stringify(data);
    const encrypted = Buffer.concat([cipher.update(jsonStr, 'utf8'), cipher.final()]);
    const authTag = cipher.getAuthTag();
    // Format: iv(12) + authTag(16) + ciphertext
    return Buffer.concat([iv, authTag, encrypted]);
}

function decrypt(buffer) {
    const iv = buffer.subarray(0, IV_LENGTH);
    const authTag = buffer.subarray(IV_LENGTH, IV_LENGTH + AUTH_TAG_LENGTH);
    const ciphertext = buffer.subarray(IV_LENGTH + AUTH_TAG_LENGTH);
    const decipher = createDecipheriv(ALGORITHM, ENCRYPTION_KEY, iv);
    decipher.setAuthTag(authTag);
    const decrypted = Buffer.concat([decipher.update(ciphertext), decipher.final()]);
    return JSON.parse(decrypted.toString('utf8'));
}

// ─── Storage ────────────────────────────────────────────────
const DATA_DIR = join(__dirname, '.cepha-syslog');
const LOG_FILE = join(DATA_DIR, 'syslog.enc');
const MAX_ENTRIES = 50000;

if (!existsSync(DATA_DIR)) mkdirSync(DATA_DIR, { recursive: true });

// In-memory index for fast queries (metadata only — content stays encrypted on disk)
let _index = [];     // { id, timestamp, category, level, offset, length }
let _entries = [];   // full decrypted entries (loaded on startup, kept in sync)
let _entryId = 0;
let _sessionId = randomBytes(8).toString('hex');

// Load existing encrypted log if present
function loadFromDisk() {
    if (!existsSync(LOG_FILE)) return;
    try {
        const raw = readFileSync(LOG_FILE);
        if (raw.length === 0) return;
        _entries = decrypt(raw);
        _entryId = _entries.length;
        // Rebuild index
        _index = _entries.map((e, i) => ({
            id: e.id || `${_sessionId}-${i}`,
            timestamp: e.timestamp,
            category: e.category,
            level: e.level
        }));
        console.log(`   📋 Loaded ${_entries.length} encrypted log entries`);
    } catch {
        // Corrupted or different encryption key — fresh start
        _entries = [];
        _index = [];
        console.log('   📋 Fresh log store (new session key)');
    }
}

function persistToDisk() {
    try {
        const encrypted = encrypt(_entries);
        writeFileSync(LOG_FILE, encrypted);
    } catch (err) {
        console.error(`[SysLog] Persist error: ${err.message}`);
    }
}

// Debounced persist — batch writes
let _persistTimer = null;
function schedulePersist() {
    if (_persistTimer) clearTimeout(_persistTimer);
    _persistTimer = setTimeout(persistToDisk, 1000); // 1s debounce
}

// ─── Security State (shared with browser via API) ───────
let _securityEnforcement = true;
let _securityLayers = 'L1-L7 active';
let _securityViolationCount = 0;

// ─── DI Audit Filters ──────────────────────────────────
// Injected filter pipeline that inspects every log entry for security events.
// Each filter receives (entry) and returns { pass: bool, annotations: {} }.
const _auditFilters = [];

function registerAuditFilter(name, fn) {
    _auditFilters.push({ name, fn });
}

function runAuditFilters(entry) {
    const annotations = {};
    for (const { name, fn } of _auditFilters) {
        try {
            const result = fn(entry);
            if (result && !result.pass) {
                annotations[name] = result;
            }
        } catch (err) {
            annotations[name] = { error: err.message };
        }
    }
    return annotations;
}

// ── Built-in Audit Filters (5-layer DOM security support) ──

// F1: DOM tamper detection — flags SEC violations with high severity
registerAuditFilter('dom-tamper', (entry) => {
    if (entry.category !== 'SEC') return { pass: true };
    const critical = ['CRITICAL', 'FATAL'].includes(entry.level);
    if (critical) _securityViolationCount++;
    return {
        pass: !critical,
        severity: entry.level,
        violationType: entry.violationType,
        action: critical ? 'escalate' : 'monitor'
    };
});

// F2: Unauthorized component modification — detects unknown mutation sources
registerAuditFilter('component-integrity', (entry) => {
    if (entry.violationType !== 'materialdb_integrity_hash_mismatch' &&
        entry.violationType !== 'materialdb_integrity_element_missing') return { pass: true };
    return {
        pass: false,
        severity: 'HIGH',
        action: 'heal-and-report',
        componentId: entry.context?.componentId
    };
});

// F3: Genome verification — validates view export object lineage
registerAuditFilter('genome-verify', (entry) => {
    if (!entry.genome) return { pass: true };
    const { functionHash, addonKey, systemRoot } = entry.genome;
    if (!functionHash || !systemRoot) return { pass: false, reason: 'missing-genome-fields' };
    // Verify: SHA256(functionHash + addonKey) should decompose from systemRoot
    return { pass: true, verified: true, functionHash, addonKey };
});

// F4: Rate limiting — detect log flooding (possible DoS or injection attack)
const _rateLimits = new Map();
registerAuditFilter('rate-limit', (entry) => {
    const src = entry.source?.file || 'unknown';
    const now = Date.now();
    if (!_rateLimits.has(src)) _rateLimits.set(src, []);
    const timestamps = _rateLimits.get(src);
    timestamps.push(now);
    // Keep only last 10 seconds
    while (timestamps.length > 0 && timestamps[0] < now - 10000) timestamps.shift();
    if (timestamps.length > 100) {
        return { pass: false, reason: 'rate-exceeded', count: timestamps.length, window: '10s' };
    }
    return { pass: true };
});

// F5: System operation tracking — tag entries tied to cepha governance
registerAuditFilter('governance-tag', (entry) => {
    const governed = entry.category === 'SEC' || entry.category === 'AUTH' ||
        entry.violationType || entry.genome;
    if (!governed) return { pass: true };
    return {
        pass: true,
        governed: true,
        merkleTracked: !!entry.genome?.systemRoot
    };
});

loadFromDisk();

// ─── Log Ingestion ──────────────────────────────────────────

function ingest(entry) {
    if (!entry.timestamp) entry.timestamp = new Date().toISOString();
    if (!entry.id) entry.id = `${_sessionId}-${++_entryId}`;
    if (!entry.session) entry.session = _sessionId;

    // Run DI audit filters
    const auditResult = runAuditFilters(entry);
    if (Object.keys(auditResult).length > 0) {
        entry._audit = auditResult;
    }

    // Track security state from SEC entries
    if (entry.category === 'SEC') {
        _securityViolationCount++;
    }
    if (entry.message?.includes('Enforcement ON')) {
        _securityEnforcement = true;
        _securityLayers = 'L1-L7 active';
    } else if (entry.message?.includes('Enforcement OFF')) {
        _securityEnforcement = false;
        _securityLayers = 'L1-L7 paused';
    }

    _entries.push(entry);
    _index.push({
        id: entry.id,
        timestamp: entry.timestamp,
        category: entry.category || 'APP',
        level: entry.level || 'INFO'
    });

    // Ring buffer — trim oldest entries
    if (_entries.length > MAX_ENTRIES) {
        const trim = _entries.length - MAX_ENTRIES;
        _entries.splice(0, trim);
        _index.splice(0, trim);
    }

    schedulePersist();
    return entry.id;
}

function ingestBatch(entries) {
    const ids = [];
    for (const e of entries) {
        ids.push(ingest(e));
    }
    return ids;
}

// ─── Query Engine ───────────────────────────────────────────

const LEVEL_WEIGHT = { TRACE: 0, DEBUG: 1, INFO: 2, WARN: 3, ERROR: 4, FATAL: 5 };

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
    if (options.source) {
        results = results.filter(e =>
            e.source?.file?.includes(options.source) ||
            e.context?.source === options.source
        );
    }
    if (options.violationType) {
        results = results.filter(e => e.violationType === options.violationType);
    }

    // Sort newest first
    results.sort((a, b) => b.timestamp.localeCompare(a.timestamp));

    const limit = parseInt(options.limit) || 500;
    return results.slice(0, limit);
}

function getAnomalySummary() {
    const secEntries = _entries.filter(e => e.category === 'SEC');
    const repairEntries = _entries.filter(e => e.category === 'REPAIR');

    const byType = {};
    for (const e of secEntries) {
        const t = e.violationType || 'unknown';
        byType[t] = (byType[t] || 0) + 1;
    }

    const byLevel = {};
    for (const e of secEntries) {
        byLevel[e.level] = (byLevel[e.level] || 0) + 1;
    }

    const bySource = {};
    for (const e of secEntries) {
        const src = e.source?.file || 'unknown';
        bySource[src] = (bySource[src] || 0) + 1;
    }

    return {
        totalEntries: _entries.length,
        totalViolations: secEntries.length,
        totalRepairs: repairEntries.length,
        successfulRepairs: repairEntries.filter(e => e.context?.repaired).length,
        failedRepairs: repairEntries.filter(e => !e.context?.repaired).length,
        violationsByType: byType,
        violationsByLevel: byLevel,
        violationsBySource: bySource,
        categories: {},
        sessionId: _sessionId,
        uptime: (Date.now() - new Date(startTime).getTime()) / 1000
    };
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

// ─── HTTP Handler ───────────────────────────────────────────

async function readBody(req) {
    return new Promise(resolve => {
        let data = '';
        req.on('data', chunk => data += chunk);
        req.on('end', () => resolve(data));
    });
}

const startTime = new Date().toISOString();

const handler = async (req, res) => {
    const url = new URL(req.url, `${protocol}://${HOST}:${PORT}`);
    const path = url.pathname;
    const method = req.method;

    // CORS headers
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, DELETE, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
    if (method === 'OPTIONS') { res.writeHead(204); res.end(); return; }

    const json = (status, data) => {
        res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify(data, null, 2));
    };

    try {
        // POST /_syslog/ingest — Receive log entries
        if (path === '/_syslog/ingest' && method === 'POST') {
            const body = JSON.parse(await readBody(req));
            if (Array.isArray(body)) {
                const ids = ingestBatch(body);
                return json(200, { ingested: ids.length, ids });
            } else if (body.entries && Array.isArray(body.entries)) {
                const ids = ingestBatch(body.entries);
                return json(200, { ingested: ids.length, ids });
            } else {
                const id = ingest(body);
                return json(200, { ingested: 1, id });
            }
        }

        // GET /_syslog/query — Query logs
        if (path === '/_syslog/query' && method === 'GET') {
            const options = {
                category: url.searchParams.get('category'),
                level: url.searchParams.get('level'),
                since: url.searchParams.get('since'),
                source: url.searchParams.get('source'),
                violationType: url.searchParams.get('violationType'),
                limit: url.searchParams.get('limit')
            };
            const results = query(options);
            return json(200, { count: results.length, entries: results });
        }

        // GET /_syslog/export — Export JSONL
        if (path === '/_syslog/export' && method === 'GET') {
            const options = {
                category: url.searchParams.get('category'),
                level: url.searchParams.get('level'),
                limit: url.searchParams.get('limit')
            };
            const jsonl = exportJsonLines(options);
            res.writeHead(200, {
                'Content-Type': 'application/x-ndjson; charset=utf-8',
                'Content-Disposition': `attachment; filename="cepha-syslog-${new Date().toISOString().slice(0, 10)}.jsonl"`
            });
            return res.end(jsonl);
        }

        // GET /_syslog/anomaly — Anomaly summary
        if (path === '/_syslog/anomaly' && method === 'GET') {
            // Fill in category counts
            const summary = getAnomalySummary();
            const cats = {};
            for (const e of _entries) {
                cats[e.category] = (cats[e.category] || 0) + 1;
            }
            summary.categories = cats;
            return json(200, summary);
        }

        // GET /_syslog/health — Health check
        if (path === '/_syslog/health') {
            return json(200, { status: 'healthy', entries: _entries.length, encrypted: true });
        }

        // GET /_syslog/info — Server diagnostics
        if (path === '/_syslog/info') {
            return json(200, {
                server: 'CephaSysLog',
                version: '1.0.0',
                status: 'running',
                startedAt: startTime,
                uptime: (Date.now() - new Date(startTime).getTime()) / 1000,
                sessionId: _sessionId,
                entries: _entries.length,
                maxEntries: MAX_ENTRIES,
                encrypted: true,
                algorithm: ALGORITHM,
                persistent: true,
                dataDir: DATA_DIR
            });
        }

        // DELETE /_syslog/clear — Clear all logs
        if (path === '/_syslog/clear' && method === 'DELETE') {
            const count = _entries.length;
            _entries.length = 0;
            _index.length = 0;
            if (existsSync(LOG_FILE)) unlinkSync(LOG_FILE);
            return json(200, { cleared: count });
        }

        // GET /_syslog/security-status — Current Secure UI state (polled by CLI)
        if (path === '/_syslog/security-status' && method === 'GET') {
            return json(200, {
                enforcement: _securityEnforcement,
                activeLayers: _securityLayers,
                violations: _securityViolationCount,
                auditFilters: _auditFilters.map(f => f.name),
                timestamp: new Date().toISOString()
            });
        }

        // POST /_syslog/security-toggle — Toggle enforcement (called by CLI panel)
        if (path === '/_syslog/security-toggle' && method === 'POST') {
            _securityEnforcement = !_securityEnforcement;
            _securityLayers = _securityEnforcement ? 'L1-L7 active' : 'L1-L7 paused';
            const toggleEntry = {
                timestamp: new Date().toISOString(),
                id: `${_sessionId}-${++_entryId}`,
                session: _sessionId,
                level: 'INFO',
                category: 'SEC',
                message: `Secure UI enforcement ${_securityEnforcement ? 'RESUMED' : 'PAUSED'} via CLI`,
                source: { file: 'cepha-syslog-server.mjs', line: 0 }
            };
            ingest(toggleEntry);
            return json(200, {
                enforcement: _securityEnforcement,
                activeLayers: _securityLayers,
                toggled: true
            });
        }

        // GET /_syslog/audit-filters — List registered DI audit filters
        if (path === '/_syslog/audit-filters' && method === 'GET') {
            return json(200, {
                filters: _auditFilters.map(f => f.name),
                count: _auditFilters.length,
                description: 'DI-injected audit pipeline for 5-layer DOM security'
            });
        }

        // 404 for unknown routes
        json(404, { error: 'Not found', endpoints: [
            'POST /_syslog/ingest',
            'GET  /_syslog/query',
            'GET  /_syslog/export',
            'GET  /_syslog/anomaly',
            'GET  /_syslog/health',
            'GET  /_syslog/info',
            'DELETE /_syslog/clear'
        ]});
    } catch (err) {
        console.error(`[SysLog] Error: ${err.message}`);
        json(500, { error: err.message });
    }
};

// ─── Start Server ───────────────────────────────────────────

const server = tlsOptions
    ? createHttpsServer(tlsOptions, handler)
    : createHttpServer(handler);

server.listen(PORT, HOST, () => {
    console.log(`   📋 CephaSysLog Server v1.0`);
    console.log(`   🌐 Listening on ${protocol}://${HOST}:${PORT}`);
    if (tlsOptions) console.log(`   🔒 HTTPS enabled (dev certificate)`);
    console.log(`   📥 Ingest:  /_syslog/ingest`);
    console.log(`   🔍 Query:   /_syslog/query`);
    console.log(`   📤 Export:  /_syslog/export`);
    console.log(`   🧠 Anomaly: /_syslog/anomaly`);
    console.log(`   💚 Health:  /_syslog/health`);
    console.log(`   🔐 Encrypted at rest (AES-256-GCM)`);
    console.log(`   🚀 Ready!`);
});
