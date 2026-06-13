// ╔══════════════════════════════════════════════════════════════╗
// ║  🧬 Cepha Ollama Proxy — NetContainer.PORTABLE              ║
// ║  Proxies /api/chat/stream → Ollama /api/generate             ║
// ║  NDJSON output compatible with CephaChat view                ║
// ╚══════════════════════════════════════════════════════════════╝

import { createServer as createHttpsServer } from 'node:https';
import { createServer as createHttpServer } from 'node:http';
import { readFileSync, existsSync } from 'node:fs';
import { request as httpRequest } from 'node:http';

const PORT = parseInt(process.env.OLLAMA_PROXY_PORT || '3005', 10);
const OLLAMA_HOST = process.env.OLLAMA_HOST || 'http://localhost:11434';
const OLLAMA_MODEL = process.env.OLLAMA_MODEL || 'llama3.2';

// ── CORS headers ───────────────────────────────────────────────
const CORS = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type'
};
function cors(res) {
    for (const [k, v] of Object.entries(CORS)) res.setHeader(k, v);
}

// Prevent crash on unhandled errors
process.on('uncaughtException', (err) => {
    console.error('⚠️ Uncaught:', err.message);
});

// ── Ollama streaming proxy ─────────────────────────────────────
function proxyToOllama(prompt, res) {
    const url = new URL('/api/generate', OLLAMA_HOST);
    const body = JSON.stringify({ model: OLLAMA_MODEL, prompt, stream: true });

    const req = httpRequest(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' }
    }, (ollamaRes) => {
        if (ollamaRes.statusCode !== 200) {
            res.writeHead(502, { 'Content-Type': 'application/x-ndjson', ...CORS });
            res.end(JSON.stringify({ type: 'char', value: '⚠️ Ollama returned ' + ollamaRes.statusCode }) + '\n');
            return;
        }

        res.writeHead(200, {
            'Content-Type': 'application/x-ndjson',
            'Cache-Control': 'no-cache',
            'Transfer-Encoding': 'chunked',
            ...CORS
        });

        // Emit start marker
        res.write(JSON.stringify({ type: 'start', user: '🧬 Ollama (' + OLLAMA_MODEL + ')' }) + '\n');

        let buffer = '';
        let doneSent = false;
        ollamaRes.on('data', (chunk) => {
            buffer += chunk.toString();
            const lines = buffer.split('\n');
            buffer = lines.pop() || '';

            for (const line of lines) {
                if (!line.trim()) continue;
                try {
                    const obj = JSON.parse(line);
                    if (obj.response) {
                        for (const ch of obj.response) {
                            res.write(JSON.stringify({ type: 'char', value: ch }) + '\n');
                        }
                    }
                    if (obj.done && !doneSent) {
                        res.write(JSON.stringify({ type: 'done' }) + '\n');
                        doneSent = true;
                    }
                } catch (_) {}
            }
        });

        ollamaRes.on('end', () => {
            if (buffer.trim()) {
                try {
                    const obj = JSON.parse(buffer);
                    if (obj.response) {
                        for (const ch of obj.response) {
                            res.write(JSON.stringify({ type: 'char', value: ch }) + '\n');
                        }
                    }
                } catch (_) {}
            }
            if (!doneSent) {
                res.write(JSON.stringify({ type: 'done' }) + '\n');
            }
            res.end();
        });
    });

    req.on('error', (err) => {
        if (!res.headersSent) {
            res.writeHead(502, { 'Content-Type': 'application/x-ndjson', ...CORS });
        }
        res.write(JSON.stringify({ type: 'start', user: '🧬 Ollama' }) + '\n');
        res.write(JSON.stringify({ type: 'char', value: '⚠️ Cannot reach Ollama: ' + err.message }) + '\n');
        res.write(JSON.stringify({ type: 'done' }) + '\n');
        res.end();
    });

    req.write(body);
    req.end();
}

// ── Health check for Ollama ────────────────────────────────────
function checkOllama(callback) {
    const url = new URL('/api/tags', OLLAMA_HOST);
    const req = httpRequest(url, { timeout: 2000 }, (r) => {
        let data = '';
        r.on('data', c => data += c);
        r.on('end', () => {
            try {
                const tags = JSON.parse(data);
                callback(null, tags.models || []);
            } catch (e) { callback(e); }
        });
    });
    req.on('error', callback);
    req.on('timeout', () => { req.destroy(); callback(new Error('timeout')); });
    req.end();
}

// ── Request handler ────────────────────────────────────────────
function handler(req, res) {
    cors(res);

    if (req.method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
    }

    // Health / status
    if (req.url === '/_ollama/health' || req.url === '/') {
        checkOllama((err, models) => {
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({
                service: 'cepha-ollama-proxy',
                ollama: err ? 'unreachable' : 'connected',
                model: OLLAMA_MODEL,
                models: models?.map(m => m.name) || [],
                error: err?.message
            }));
        });
        return;
    }

    // Chat stream — GET /api/chat/stream?message=...
    if (req.url?.startsWith('/api/chat/stream')) {
        const params = new URL(req.url, 'http://localhost').searchParams;
        const message = params.get('message') || 'Hello';
        proxyToOllama(message, res);
        return;
    }

    // 404
    res.writeHead(404, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: 'Not found', routes: ['/api/chat/stream', '/_ollama/health'] }));
}

// ── Start server ───────────────────────────────────────────────
function findCert() {
    const certEnv = process.env.CEPHA_CERT;
    const keyEnv = process.env.CEPHA_KEY;
    if (certEnv && keyEnv && existsSync(certEnv) && existsSync(keyEnv)) {
        return { cert: readFileSync(certEnv), key: readFileSync(keyEnv) };
    }
    return null;
}

const tls = findCert();
const server = tls
    ? createHttpsServer(tls, handler)
    : createHttpServer(handler);

server.listen(PORT, '0.0.0.0', () => {
    const proto = tls ? 'https' : 'http';
    console.log(`🧬 Cepha Ollama Proxy → ${proto}://localhost:${PORT}`);
    console.log(`   Ollama host: ${OLLAMA_HOST} | model: ${OLLAMA_MODEL}`);

    checkOllama((err, models) => {
        if (err) {
            console.log(`   ⚠️  Ollama not reachable: ${err.message}`);
            console.log(`   💡 Start Ollama: ollama serve`);
        } else {
            console.log(`   ✅ Ollama connected — ${models.length} model(s) available`);
        }
    });
});
