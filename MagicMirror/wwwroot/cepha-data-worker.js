// 🧬 Cepha Data Worker — Handles heavy data operations off the main thread
// Uses OPFS (Origin Private File System) for persistent binary storage
// Communicates via PostMessage protocol

const PROTOCOL_VERSION = 1;
let opfsRoot = null;

// ─── OPFS Initialization ─────────────────────────────────────

async function initOPFS() {
    if (opfsRoot) return opfsRoot;
    if (!navigator.storage?.getDirectory) {
        postMessage({ type: 'error', error: 'OPFS not supported in this browser' });
        return null;
    }
    opfsRoot = await navigator.storage.getDirectory();
    return opfsRoot;
}

async function getOrCreateDir(path) {
    const root = await initOPFS();
    if (!root) return null;
    const parts = path.split('/').filter(Boolean);
    let dir = root;
    for (const part of parts) {
        dir = await dir.getDirectoryHandle(part, { create: true });
    }
    return dir;
}

// ─── File Operations ─────────────────────────────────────────

async function writeFile(path, data) {
    const parts = path.split('/');
    const fileName = parts.pop();
    const dir = parts.length > 0 ? await getOrCreateDir(parts.join('/')) : await initOPFS();
    if (!dir) throw new Error('OPFS unavailable');

    const fileHandle = await dir.getFileHandle(fileName, { create: true });
    const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : new Uint8Array(data);

    // Try createSyncAccessHandle with retry (exclusive lock may be held by another tab)
    if (typeof fileHandle.createSyncAccessHandle === 'function') {
        for (let attempt = 0; attempt < 5; attempt++) {
            try {
                const access = await fileHandle.createSyncAccessHandle();
                access.truncate(0);
                access.write(bytes);
                access.flush();
                access.close();
                return true;
            } catch (e) {
                if (e.name === 'NoModificationAllowedError' || e.name === 'InvalidStateError') {
                    // Another tab holds the lock — wait and retry
                    await new Promise(r => setTimeout(r, 50 * (attempt + 1)));
                    continue;
                }
                throw e;
            }
        }
    }

    // Fallback to createWritable (no exclusive lock needed)
    const writable = await fileHandle.createWritable();
    await writable.write(bytes);
    await writable.close();
    return true;
}

async function readFile(path, asText = false) {
    const parts = path.split('/');
    const fileName = parts.pop();
    const dir = parts.length > 0 ? await getOrCreateDir(parts.join('/')) : await initOPFS();
    if (!dir) throw new Error('OPFS unavailable');

    try {
        const fileHandle = await dir.getFileHandle(fileName);
        // Use getFile() for reads — non-exclusive, allows concurrent access
        // from multiple tabs. createSyncAccessHandle() is exclusive and would
        // block other tabs from reading simultaneously.
        const file = await fileHandle.getFile();
        return asText ? await file.text() : await file.arrayBuffer();
    } catch (e) {
        if (e.name === 'NotFoundError') return null;
        throw e;
    }
}

async function deleteFile(path) {
    const parts = path.split('/');
    const fileName = parts.pop();
    const dir = parts.length > 0 ? await getOrCreateDir(parts.join('/')) : await initOPFS();
    if (!dir) throw new Error('OPFS unavailable');
    try {
        await dir.removeEntry(fileName);
        return true;
    } catch { return false; }
}

async function listFiles(path = '') {
    const dir = path ? await getOrCreateDir(path) : await initOPFS();
    if (!dir) return [];
    const entries = [];
    for await (const [name, handle] of dir.entries()) {
        entries.push({ name, kind: handle.kind });
    }
    return entries;
}

async function getFileSize(path) {
    const parts = path.split('/');
    const fileName = parts.pop();
    const dir = parts.length > 0 ? await getOrCreateDir(parts.join('/')) : await initOPFS();
    if (!dir) return -1;
    try {
        const fileHandle = await dir.getFileHandle(fileName);
        const file = await fileHandle.getFile();
        return file.size;
    } catch { return -1; }
}

// ─── Database Sync (OPFS as staging area) ────────────────────

async function exportDbToOPFS(dbBytes) {
    await writeFile('cepha/db-snapshot.bin', dbBytes);
    await writeFile('cepha/db-snapshot.meta', JSON.stringify({
        timestamp: Date.now(),
        size: dbBytes.byteLength
    }));
    return true;
}

async function importDbFromOPFS() {
    const data = await readFile('cepha/db-snapshot.bin');
    const meta = await readFile('cepha/db-snapshot.meta', true);
    return { data, meta: meta ? JSON.parse(meta) : null };
}

// ─── Pending Changes Queue (for offline sync) ────────────────

async function enqueuePendingChange(change) {
    const queueStr = await readFile('cepha/pending-queue.json', true);
    const queue = queueStr ? JSON.parse(queueStr) : [];
    queue.push({ ...change, queuedAt: Date.now() });
    await writeFile('cepha/pending-queue.json', JSON.stringify(queue));
    return queue.length;
}

async function dequeuePendingChanges() {
    const queueStr = await readFile('cepha/pending-queue.json', true);
    const queue = queueStr ? JSON.parse(queueStr) : [];
    if (queue.length > 0) {
        await writeFile('cepha/pending-queue.json', '[]');
    }
    return queue;
}

async function getPendingCount() {
    const queueStr = await readFile('cepha/pending-queue.json', true);
    const queue = queueStr ? JSON.parse(queueStr) : [];
    return queue.length;
}

// ─── Stats ───────────────────────────────────────────────────

async function getStorageStats() {
    const estimate = navigator.storage?.estimate ? await navigator.storage.estimate() : null;
    const dbSize = await getFileSize('cepha/db-snapshot.bin');
    const pendingCount = await getPendingCount();
    return {
        opfsAvailable: !!opfsRoot,
        quota: estimate?.quota ?? -1,
        usage: estimate?.usage ?? -1,
        dbSnapshotSize: dbSize,
        pendingChanges: pendingCount
    };
}

// ─── Message Handler ─────────────────────────────────────────

self.onmessage = async (event) => {
    const { id, type, payload } = event.data;
    try {
        let result;
        switch (type) {
            case 'init':
                await initOPFS();
                result = { ok: true, version: PROTOCOL_VERSION };
                break;

            // File operations
            case 'write':
                result = await writeFile(payload.path, payload.data);
                break;
            case 'read':
                result = await readFile(payload.path, payload.asText);
                break;
            case 'delete':
                result = await deleteFile(payload.path);
                break;
            case 'list':
                result = await listFiles(payload?.path);
                break;

            // Database sync
            case 'db-export':
                result = await exportDbToOPFS(payload.data);
                break;
            case 'db-import':
                result = await importDbFromOPFS();
                break;

            // Pending changes queue
            case 'enqueue':
                result = await enqueuePendingChange(payload);
                break;
            case 'dequeue':
                result = await dequeuePendingChanges();
                break;
            case 'pending-count':
                result = await getPendingCount();
                break;

            // Stats
            case 'stats':
                result = await getStorageStats();
                break;

            default:
                throw new Error(`Unknown message type: ${type}`);
        }
        postMessage({ id, type: 'result', result });
    } catch (error) {
        postMessage({ id, type: 'error', error: error.message });
    }
};

// ─── Ready signal ────────────────────────────────────────────
postMessage({ type: 'ready', version: PROTOCOL_VERSION });
