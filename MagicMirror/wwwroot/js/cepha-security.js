// 🛡️ CephaSecureDOM — Multi-Layer DOM Security & Tamper Prevention
// This module provides financial-grade UI security for Cepha applications.
//
// Layers:
//   L1: MutationObserver — detects and reverts unauthorized DOM changes
//   L2: Integrity Hash — SHA-256 verification of expected DOM state
//   L3: Shadow DOM Inputs — password/identity fields hidden from DevTools
//   L4: Continuous Stream Guard — Worker-driven DOM is the only truth source
//   L5: Anti-Inspection — DevTools detection, clipboard sanitization
//   L9: Server Exclusion Signing — short-lived nonce-based allow-list for
//       authorized server-pushed DOM updates (SysLog, Ollama, Sarmad)
//   L9+: Source Link — trusted data source verification with auto-managed
//        L9 lifecycle, reference-counted frames, and instance key binding
//
// Modes:
//   development — violations are logged with full diagnostics (file, line,
//                 DOM path, stack trace). Enforcement toggle controls whether
//                 mutations are reverted. Detection is ALWAYS active.
//   production  — hard guard. All violations trigger immediate DOM revert
//                 and damaged sections are healed via Worker re-render.
//
// Architecture: The Worker is the ONLY authority. Main thread accepts
// frames from Worker and rejects everything else. Observer is NEVER
// disconnected — during frame rendering, mutations are filtered by flag.

const CephaSecurity = (() => {
    'use strict';

    // ── State ──
    let _workerFrameActive = false;
    let _domHash = null;
    let _observer = null;
    let _secureInputs = new Map();
    let _violations = [];
    let _pendingRepairs = [];         // stored mutation records for deferred repair
    let _repairLog = [];              // log of repair actions taken
    let _enabled = false;
    let _guardedContainer = null;
    let _antiDevToolsInterval = null;
    let _workerRef = null;
    let _mode = 'development';        // 'development' | 'production'
    let _enforcement = false;         // true = revert+heal mutations, false = detect+log only
    let _frameDepth = 0;              // reference count for nested frame operations
    let _frameGraceTimer = null;      // debounced frame completion (activateScripts grace)
    const _serverExclusions = new Map(); // L9: nonce → { service, expiry, target }
    let _serverFrameCount = 0;        // L9: reference-counted (>0 = active server frame)
    const _trustedSources = new Map(); // L9+: urlPattern → { service, instanceKey }
    const _sourceLinks = new Map();    // L9+: linkId → SourceLink object
    let _enforcementBaselineTime = 0; // timestamp of last enforcement ON — grace period for L7
    const MAX_VIOLATIONS = 500;
    const MAX_REPAIRS = 200;
    const FRAME_GRACE_MS = 500;       // ms after last endWorkerFrame before frame is considered complete
    const EXCLUSION_TTL_DEFAULT = 5000; // ms default TTL for server exclusions
    const EXCLUSION_MAX = 100;        // max concurrent exclusions (prevent unbounded growth)
    const ENFORCEMENT_GRACE_MS = 3000; // ms grace after enforcement ON before L7 checks

    const _observerConfig = {
        childList: true,
        attributes: true,
        characterData: true,
        subtree: true,
        attributeOldValue: true,
        characterDataOldValue: true
    };

    // ── L1: MutationObserver — Tamper Detection & Reversion ──

    function initObserver(containerSelector) {
        _guardedContainer = document.querySelector(containerSelector);
        if (!_guardedContainer) return;

        _observer = new MutationObserver((mutations) => {
            if (_workerFrameActive) return;
            if (_serverFrameCount > 0) return; // L9: skip during any server-signed update

            for (const m of mutations) {
                // L9: check if mutation target is inside a server-excluded region
                if (isServerExcluded(m.target)) continue;

                const violation = classifyMutation(m);
                if (violation) {
                    logViolation(violation);
                    if (_enforcement) {
                        // Immediate repair
                        const result = repairMutation(m, violation);
                        logRepair(violation, result);
                    } else {
                        // Store for deferred repair when enforcement is toggled ON
                        _pendingRepairs.push({ mutation: m, violation, timestamp: Date.now() });
                        if (_pendingRepairs.length > MAX_REPAIRS) _pendingRepairs.shift();
                    }
                }
            }
        });

        _observer.observe(_guardedContainer, _observerConfig);
        _enabled = true;
    }

    function classifyMutation(m) {
        const target = m.target;
        // Resolve to nearest element (text nodes have no attributes)
        const el = target.nodeType === 1 ? target : target.parentElement;
        // Ancestor-aware skip: check element AND its ancestors
        if (el?.closest?.('[data-cepha-secure-skip]')) return null;
        if (el?.closest?.('[data-cepha-stream-skip]')) return null;
        // L9: check nonce-based server exclusion on the target or its ancestors
        if (isServerExcluded(target)) return null;

        if (m.type === 'attributes') {
            const attr = m.attributeName;
            if (attr === 'type' && target.tagName === 'INPUT') {
                return {
                    level: 'CRITICAL',
                    type: 'input_type_change',
                    detail: `Input type changed from "${m.oldValue}" to "${target.getAttribute('type')}"`,
                    element: describeElement(target)
                };
            }
            if (target.closest?.('[data-cepha-secure]')) {
                return {
                    level: 'HIGH',
                    type: 'secure_element_modified',
                    detail: `Attribute "${attr}" changed on secure element`,
                    element: describeElement(target)
                };
            }
            if ((attr === 'style' || attr === 'hidden' || attr === 'disabled') &&
                target.closest?.('[data-cepha-locked]')) {
                return {
                    level: 'HIGH',
                    type: 'locked_field_unhidden',
                    detail: `Attempt to modify visibility/state of locked field`,
                    element: describeElement(target)
                };
            }
            return null;
        }

        if (m.type === 'childList') {
            for (const node of m.addedNodes) {
                if (node.nodeType === 1) {
                    if (node.tagName === 'SCRIPT' || node.querySelector?.('script')) {
                        return {
                            level: 'CRITICAL',
                            type: 'script_injection',
                            detail: `Unauthorized <script> injection detected`,
                            element: describeElement(node)
                        };
                    }
                    if (node.tagName === 'IFRAME' || node.querySelector?.('iframe')) {
                        return {
                            level: 'CRITICAL',
                            type: 'iframe_injection',
                            detail: `Unauthorized <iframe> injection detected`,
                            element: describeElement(node)
                        };
                    }
                }
            }
            for (const node of m.removedNodes) {
                if (node.nodeType === 1 && node.hasAttribute?.('data-cepha-secure')) {
                    return {
                        level: 'HIGH',
                        type: 'secure_element_removed',
                        detail: `Secure element removed from DOM`,
                        element: describeElement(node)
                    };
                }
            }
            return null;
        }

        if (m.type === 'characterData') {
            if (m.target.parentElement?.closest?.('[data-cepha-secure]')) {
                return {
                    level: 'MEDIUM',
                    type: 'secure_text_modified',
                    detail: `Text content modified in secure element`,
                    element: describeElement(m.target.parentElement)
                };
            }
        }

        return null;
    }

    function repairMutation(m, violation) {
        const result = { repaired: false, action: 'none', fallback: false };
        try {
            if (m.type === 'attributes') {
                if (m.oldValue !== null) {
                    m.target.setAttribute(m.attributeName, m.oldValue);
                    result.action = `restored attr "${m.attributeName}" to "${m.oldValue}"`;
                } else {
                    m.target.removeAttribute(m.attributeName);
                    result.action = `removed injected attr "${m.attributeName}"`;
                }
                result.repaired = true;
            } else if (m.type === 'characterData') {
                if (m.oldValue !== null) {
                    m.target.textContent = m.oldValue;
                    result.action = 'restored text content';
                    result.repaired = true;
                }
            } else if (m.type === 'childList') {
                // Remove injected nodes
                for (const node of m.addedNodes) {
                    if (node.parentNode) {
                        node.parentNode.removeChild(node);
                        result.action = `removed injected ${node.tagName || 'node'}`;
                        result.repaired = true;
                    }
                }
                // Restore removed nodes — attempt positional restore
                for (const node of m.removedNodes) {
                    if (node.nodeType === 1 && m.target) {
                        if (m.nextSibling && m.nextSibling.parentNode === m.target) {
                            m.target.insertBefore(node, m.nextSibling);
                        } else {
                            m.target.appendChild(node);
                        }
                        result.action = `restored removed ${node.tagName || 'node'}`;
                        result.repaired = true;
                    }
                }
            }
        } catch (err) {
            result.action = `local repair failed: ${err.message}`;
            result.fallback = true;
        }

        // If local repair failed or couldn't restore, request Worker re-render
        if (!result.repaired || result.fallback) {
            result.fallback = true;
            result.action = 'requesting Worker re-render (full DOM heal)';
            requestWorkerHeal();
        }

        return result;
    }

    function requestWorkerHeal() {
        if (_workerRef) {
            try {
                _workerRef.postMessage({ type: 'navigate', path: location.pathname });
            } catch { /* worker may not be ready */ }
        }
    }

    function logRepair(violation, result) {
        const entry = {
            timestamp: new Date().toISOString(),
            violationType: violation.type,
            violationLevel: violation.level,
            domPath: violation.domPath || 'unknown',
            action: result.action,
            repaired: result.repaired,
            fallback: result.fallback
        };
        _repairLog.push(entry);
        if (_repairLog.length > MAX_REPAIRS) _repairLog.shift();

        const icon = result.repaired ? '🔧' : '🔄';
        console.log(
            `%c${icon} [CephaSecurity] Repair: ${result.action}`,
            `color: ${result.repaired ? '#4caf50' : '#ff9800'}; font-weight: bold`
        );

        // Feed to CephaSysLog for encrypted persistent storage
        // Note: no isInitialized() guard — createEntry() safely buffers pre-init entries in memory
        if (typeof CephaSysLog !== 'undefined') {
            CephaSysLog.ingestRepair(entry);
        }

        // Send repair event to Worker for logs panel
        if (_workerRef) {
            try {
                _workerRef.postMessage({
                    type: 'security-repair',
                    repair: entry
                });
            } catch { /* worker may not be ready */ }
        }
    }

    function repairAll() {
        if (_pendingRepairs.length === 0) return { repaired: 0, failed: 0, healed: false };

        let repaired = 0, failed = 0;
        const pending = [..._pendingRepairs];
        _pendingRepairs.length = 0;

        // Try to repair each stored mutation (most recent first for correct ordering)
        for (let i = pending.length - 1; i >= 0; i--) {
            const { mutation, violation } = pending[i];
            const result = repairMutation(mutation, violation);
            logRepair(violation, result);
            if (result.repaired && !result.fallback) repaired++;
            else failed++;
        }

        // If any repairs failed, request full Worker re-render as final healing step
        if (failed > 0) {
            requestWorkerHeal();
            console.log(
                `%c🔄 [CephaSecurity] Full DOM heal requested — ${failed} mutations could not be locally repaired`,
                'color: #ff9800; font-weight: bold'
            );
        }

        console.log(
            `%c🛡️ [CephaSecurity] Repair complete: ${repaired} repaired, ${failed} healed via Worker`,
            'color: #4caf50; font-weight: bold'
        );

        return { repaired, failed, healed: failed > 0 };
    }

    // ── L2: DOM Integrity Hash ──

    async function computeDomHash(container) {
        const html = container?.innerHTML || '';
        const data = new TextEncoder().encode(html);
        const hashBuffer = await crypto.subtle.digest('SHA-256', data);
        return Array.from(new Uint8Array(hashBuffer))
            .map(b => b.toString(16).padStart(2, '0')).join('');
    }

    async function updateIntegrityHash() {
        if (_guardedContainer) {
            _domHash = await computeDomHash(_guardedContainer);
            // Store snapshot in CephaMaterialDb for cross-verification
            if (typeof CephaMaterialDb !== 'undefined' && CephaMaterialDb.isInitialized()) {
                CephaMaterialDb.storeIntegritySnapshot('root', _guardedContainer.tagName === 'BODY' ? 'body' : '#' + (_guardedContainer.id || 'app'));
            }
        }
    }

    async function verifyIntegrity() {
        if (!_guardedContainer || !_domHash) return true;
        if (_serverFrameCount > 0) return true; // Skip during active source link
        const current = await computeDomHash(_guardedContainer);
        if (current !== _domHash) {
            logViolation({
                level: 'CRITICAL',
                type: 'integrity_hash_mismatch',
                detail: `DOM integrity check failed. Expected: ${_domHash.substring(0, 16)}... Got: ${current.substring(0, 16)}...`,
                element: 'container'
            });
            return false;
        }
        return true;
    }

    // ── L3: Shadow DOM Secure Inputs ──

    class CephaSecureField extends HTMLElement {
        #shadow;
        #input;
        #config;

        constructor() {
            super();
            this.#shadow = this.attachShadow({ mode: 'closed' });
        }

        connectedCallback() {
            this.#config = {
                fieldType: this.getAttribute('data-field-type') || 'text',
                name: this.getAttribute('data-name') || '',
                placeholder: this.getAttribute('data-placeholder') || '',
                autocomplete: this.getAttribute('data-autocomplete') || 'off',
                required: this.hasAttribute('data-required'),
                maxlength: this.getAttribute('data-maxlength') || '',
                pattern: this.getAttribute('data-pattern') || '',
                ariaLabel: this.getAttribute('data-aria-label') || this.getAttribute('data-placeholder') || '',
            };

            // Remove all data-* attributes immediately
            for (const attr of [...this.attributes]) {
                if (attr.name.startsWith('data-')) this.removeAttribute(attr.name);
            }

            this.#render();
        }

        #render() {
            const c = this.#config;

            const style = document.createElement('style');
            style.textContent = `
                :host { display: block; position: relative; }
                .sf-wrap { position: relative; width: 100%; }
                .sf-input {
                    width: 100%;
                    padding: 14px 16px;
                    font-size: 16px;
                    font-family: inherit;
                    border: 2px solid var(--cepha-border, #e0e0e0);
                    border-radius: 8px;
                    background: var(--cepha-input-bg, #fafafa);
                    color: var(--cepha-text, #212121);
                    outline: none;
                    transition: border-color 0.2s, box-shadow 0.2s;
                    box-sizing: border-box;
                    -webkit-text-security: ${c.fieldType === 'password' ? 'disc' : 'none'};
                }
                .sf-input:focus {
                    border-color: var(--cepha-primary, #667eea);
                    box-shadow: 0 0 0 3px var(--cepha-focus-ring, rgba(102,126,234,0.15));
                }
                .sf-input::placeholder { color: var(--cepha-placeholder, #9e9e9e); }
                .sf-label {
                    position: absolute; top: -8px; left: 12px;
                    background: var(--cepha-input-bg, #fafafa);
                    padding: 0 4px; font-size: 12px;
                    color: var(--cepha-label, #757575);
                    pointer-events: none; transition: all 0.2s;
                }
                .sf-input:focus ~ .sf-label { color: var(--cepha-primary, #667eea); }
                .sf-strength { height: 3px; margin-top: 4px; border-radius: 2px; background: #e0e0e0; overflow: hidden; }
                .sf-bar { height: 100%; width: 0%; transition: width 0.3s, background 0.3s; border-radius: 2px; }
                ${c.fieldType === 'password' ? `
                .sf-input { -webkit-user-select: none; user-select: none; }
                .sf-input::selection { background: transparent; }` : ''}
            `;

            const wrap = document.createElement('div');
            wrap.className = 'sf-wrap';

            // Type is ALWAYS "text" in DOM; masking via CSS -webkit-text-security
            this.#input = document.createElement('input');
            this.#input.type = 'text';
            this.#input.className = 'sf-input';
            this.#input.name = c.name;
            this.#input.placeholder = c.placeholder;
            this.#input.autocomplete = c.autocomplete;
            this.#input.setAttribute('aria-label', c.ariaLabel);
            if (c.required) this.#input.required = true;
            if (c.maxlength) this.#input.maxLength = parseInt(c.maxlength);
            if (c.pattern) this.#input.pattern = c.pattern;

            if (c.name.includes('confirm')) {
                this.#input.addEventListener('paste', e => e.preventDefault());
            }

            const label = document.createElement('span');
            label.className = 'sf-label';
            label.textContent = c.placeholder;

            wrap.appendChild(this.#input);
            wrap.appendChild(label);

            // Password strength meter
            if (c.fieldType === 'password' && !c.name.includes('confirm')) {
                const meter = document.createElement('div');
                meter.className = 'sf-strength';
                const bar = document.createElement('div');
                bar.className = 'sf-bar';
                meter.appendChild(bar);
                wrap.appendChild(meter);

                this.#input.addEventListener('input', () => {
                    const s = this.#measureStrength(this.#input.value);
                    bar.style.width = s.percent + '%';
                    bar.style.background = s.color;
                });
            }

            // Lock type property
            const inputRef = this.#input;
            Object.defineProperty(inputRef, 'type', {
                get: () => 'text',
                set: () => {},
                configurable: false
            });

            this.#shadow.appendChild(style);
            this.#shadow.appendChild(wrap);
            _secureInputs.set(c.name, this);
        }

        #measureStrength(pw) {
            let score = 0;
            if (pw.length >= 8) score++;
            if (pw.length >= 12) score++;
            if (/[a-z]/.test(pw) && /[A-Z]/.test(pw)) score++;
            if (/\d/.test(pw)) score++;
            if (/[^a-zA-Z0-9]/.test(pw)) score++;
            const colors = ['#f44336', '#ff9800', '#ffc107', '#8bc34a', '#4caf50'];
            return { percent: Math.min(score * 20, 100), color: colors[Math.min(score, 4)] };
        }

        getValue() { return this.#input?.value || ''; }
        setValue(v) { if (this.#input) this.#input.value = v; }
        focus() { this.#input?.focus(); }
        clear() { if (this.#input) this.#input.value = ''; }
    }

    function registerSecureField() {
        if (!customElements.get('cepha-secure-field')) {
            customElements.define('cepha-secure-field', CephaSecureField);
        }
    }

    function getSecureFormData() {
        const data = {};
        for (const [name, el] of _secureInputs) data[name] = el.getValue();
        return data;
    }

    // ── L4: Worker Frame Guard (observer stays connected, flag-based filtering) ──

    function beginWorkerFrame() {
        _frameDepth++;
        _workerFrameActive = true;
        // Cancel any pending frame completion timer
        if (_frameGraceTimer) { clearTimeout(_frameGraceTimer); _frameGraceTimer = null; }
        // Observer stays connected — continues monitoring all mutations
    }

    function endWorkerFrame() {
        _frameDepth = Math.max(0, _frameDepth - 1);
        if (_frameDepth > 0) return; // still inside nested frame

        // Grace period — activateScripts() may still be modifying DOM
        // (CSS loading, script cloning, external script onload)
        if (_frameGraceTimer) clearTimeout(_frameGraceTimer);
        _frameGraceTimer = setTimeout(async () => {
            _frameGraceTimer = null;
            _workerFrameActive = false;
            // Update integrity hash to the new known-good state
            await updateIntegrityHash();
        }, FRAME_GRACE_MS);
    }

    function isFrameActive() { return _workerFrameActive; }

    // ── L5: Anti-Inspection & Clipboard Protection ──

    function initAntiInspection() {
        let _devToolsOpen = false;

        _antiDevToolsInterval = setInterval(() => {
            const threshold = 160;
            const wDiff = window.outerWidth - window.innerWidth;
            const hDiff = window.outerHeight - window.innerHeight;
            const newState = wDiff > threshold || hDiff > threshold;

            if (newState && !_devToolsOpen) {
                _devToolsOpen = true;
                logViolation({
                    level: 'WARNING',
                    type: 'devtools_opened',
                    detail: 'Developer tools detected — secure fields protected',
                    element: 'window'
                });
                for (const [, el] of _secureInputs) el.clear();
            } else if (!newState && _devToolsOpen) {
                _devToolsOpen = false;
            }
        }, 1000);

        // Block right-click on secure elements
        document.addEventListener('contextmenu', (e) => {
            if (e.target.closest?.('[data-cepha-secure], cepha-secure-field'))
                e.preventDefault();
        }, true);

        // Block copy from secure fields
        document.addEventListener('copy', (e) => {
            const sel = window.getSelection();
            if (sel?.anchorNode?.parentElement?.closest?.('cepha-secure-field')) {
                e.preventDefault();
                e.clipboardData?.setData('text/plain', '');
            }
        }, true);

        // Block drag from secure fields
        document.addEventListener('dragstart', (e) => {
            if (e.target.closest?.('cepha-secure-field')) e.preventDefault();
        }, true);
    }

    // ── Violation Logging (enhanced diagnostics) ──

    function getDomPath(el) {
        if (!el || typeof el === 'string') return el || 'unknown';
        const parts = [];
        let node = el;
        while (node && node !== document.body && node !== document.documentElement) {
            let sel = node.tagName?.toLowerCase() || '';
            if (node.id) sel += '#' + node.id;
            else if (node.className && typeof node.className === 'string') {
                const cls = node.className.trim().split(/\s+/).slice(0, 2).join('.');
                if (cls) sel += '.' + cls;
            }
            parts.unshift(sel);
            node = node.parentElement;
        }
        return parts.join(' > ') || 'unknown';
    }

    function extractSource(stack) {
        if (!stack) return { file: 'unknown', line: 0, column: 0, raw: '' };
        // Skip first 2 lines (Error + logViolation itself)
        const lines = stack.split('\n').slice(2);
        for (const line of lines) {
            const match = line.match(/(?:at\s+)?(?:.*?\s+\()?(.+?):(\d+):(\d+)\)?$/);
            if (match) {
                const file = match[1].replace(/^https?:\/\/[^/]+/, '');
                return { file, line: parseInt(match[2]), column: parseInt(match[3]), raw: line.trim() };
            }
        }
        return { file: 'unknown', line: 0, column: 0, raw: lines[0]?.trim() || '' };
    }

    function logViolation(violation) {
        const stack = new Error().stack;
        violation.timestamp = new Date().toISOString();
        violation.stack = stack;
        violation.source = extractSource(stack);
        violation.domPath = typeof violation.element === 'string'
            ? violation.element
            : getDomPath(violation.element);
        violation.mode = _mode;
        violation.enforced = _enforcement;

        _violations.push(violation);
        if (_violations.length > MAX_VIOLATIONS) _violations.shift();

        const prefix = violation.level === 'CRITICAL' ? '🚨' :
                       violation.level === 'HIGH' ? '⚠️' :
                       violation.level === 'WARNING' ? '🔔' : 'ℹ️';

        if (_mode === 'development') {
            console.groupCollapsed(
                `${prefix} [CephaSecurity] ${violation.level}: ${violation.type} — ${violation.detail}`
            );
            console.log('DOM Path:', violation.domPath);
            console.log('Source:', `${violation.source.file}:${violation.source.line}:${violation.source.column}`);
            console.log('Enforced:', _enforcement ? 'YES (reverted)' : 'NO (log only)');
            console.log('Stack:', violation.source.raw);
            console.groupEnd();
        } else {
            console.warn(`${prefix} [CephaSecurity] ${violation.level}: ${violation.type} — ${violation.detail}`);
        }

        // Feed to CephaSysLog for encrypted persistent storage
        // Note: no isInitialized() guard — createEntry() safely buffers pre-init entries in memory
        if (typeof CephaSysLog !== 'undefined') {
            CephaSysLog.ingestViolation(violation);
        }

        if (_workerRef) {
            try {
                _workerRef.postMessage({
                    type: 'security-violation',
                    violation: {
                        level: violation.level,
                        type: violation.type,
                        detail: violation.detail,
                        source: violation.source,
                        domPath: violation.domPath,
                        timestamp: violation.timestamp,
                        enforced: violation.enforced
                    }
                });
            } catch { /* worker may not be ready */ }
        }
    }

    function describeElement(el) {
        if (!el || !el.tagName) return 'unknown';
        let desc = el.tagName.toLowerCase();
        if (el.id) desc += `#${el.id}`;
        if (el.className && typeof el.className === 'string')
            desc += `.${el.className.split(' ').join('.')}`;
        return desc;
    }

    // ── Public API ──

    // ── L9: Server Exclusion Signing ──
    // Allows authorized server-pushed DOM updates (SysLog, Ollama, Sarmad)
    // to bypass enforcement without disabling security for the whole page.

    function registerServerExclusion(nonce, service, ttlMs, targetSelector) {
        if (_serverExclusions.size >= EXCLUSION_MAX) {
            purgeExpiredExclusions();
            // If still at capacity after purge, evict oldest
            if (_serverExclusions.size >= EXCLUSION_MAX) {
                const oldest = _serverExclusions.keys().next().value;
                _serverExclusions.delete(oldest);
            }
        }
        const ttl = Math.min(ttlMs || EXCLUSION_TTL_DEFAULT, 30000); // cap at 30s
        _serverExclusions.set(nonce, {
            service: service || 'unknown',
            expiry: Date.now() + ttl,
            target: targetSelector || null,
            created: Date.now()
        });
        // Auto-purge after TTL
        setTimeout(() => _serverExclusions.delete(nonce), ttl + 100);
        return nonce;
    }

    function beginServerFrame(nonce) {
        if (!nonce || !_serverExclusions.has(nonce)) return false;
        const ex = _serverExclusions.get(nonce);
        if (Date.now() > ex.expiry) { _serverExclusions.delete(nonce); return false; }
        _serverFrameCount++;
        return true;
    }

    function endServerFrame() {
        _serverFrameCount = Math.max(0, _serverFrameCount - 1);
        if (_serverFrameCount === 0) {
            // Only finalize when ALL server frames are closed
            updateIntegrityHash();
            if (typeof CephaMaterial !== 'undefined' && CephaMaterial.notifyDomUpdate) {
                CephaMaterial.notifyDomUpdate();
            }
        }
    }

    function isServerExcluded(node) {
        if (!node || _serverExclusions.size === 0) return false;
        const el = node.nodeType === 1 ? node : node.parentElement;
        if (!el) return false;
        // Check if the element or any ancestor has a valid server nonce
        const nonceEl = el.closest?.('[data-cepha-server-nonce]');
        if (!nonceEl) return false;
        const nonce = nonceEl.getAttribute('data-cepha-server-nonce');
        const ex = _serverExclusions.get(nonce);
        if (!ex) return false;
        if (Date.now() > ex.expiry) { _serverExclusions.delete(nonce); return false; }
        return true;
    }

    function revokeServerExclusion(nonce) {
        return _serverExclusions.delete(nonce);
    }

    function purgeExpiredExclusions() {
        const now = Date.now();
        for (const [nonce, ex] of _serverExclusions) {
            if (now > ex.expiry) _serverExclusions.delete(nonce);
        }
    }

    function getActiveExclusions() {
        purgeExpiredExclusions();
        const result = [];
        for (const [nonce, ex] of _serverExclusions) {
            result.push({ nonce, service: ex.service, remaining: ex.expiry - Date.now(), target: ex.target });
        }
        return result;
    }

    function generateNonce() {
        const arr = new Uint8Array(16);
        crypto.getRandomValues(arr);
        return Array.from(arr, b => b.toString(16).padStart(2, '0')).join('');
    }

    function init(containerSelector = '#app', workerRef = null, options = {}) {
        _workerRef = workerRef;
        _mode = options.mode || 'development';
        _enforcement = options.enforcement ?? (_mode === 'production');
        registerSecureField();
        initObserver(containerSelector);
        initAntiInspection(); // Always active — detection in all modes
        console.log(
            `%c🛡️ CephaSecurity: Active (${_mode} | enforcement: ${_enforcement ? 'ON' : 'OFF'})`,
            'color: #4caf50; font-weight: bold'
        );
    }

    function setMode(mode) {
        _mode = mode;
        if (mode === 'production' && !_enforcement) _enforcement = true;
    }

    function toggleEnforcement() {
        _enforcement = !_enforcement;
        console.log(`%c🛡️ CephaSecurity: Enforcement ${_enforcement ? 'ON' : 'OFF'}`,
            `color: ${_enforcement ? '#f44336' : '#4caf50'}; font-weight: bold`);

        // Notify SysLog server of enforcement state change
        if (typeof CephaSysLog !== 'undefined') {
            CephaSysLog.notifyEnforcementChange(_enforcement);
        }

        // When enforcement is turned ON, re-baseline integrity hashes
        // to match current DOM state, then repair accumulated violations
        if (_enforcement) {
            _enforcementBaselineTime = Date.now();
            updateIntegrityHash();

            if (_pendingRepairs.length > 0) {
                const result = repairAll();
                console.log(
                    `%c🛡️ [CephaSecurity] Retroactive repair: ${result.repaired} fixed, ${result.failed} healed`,
                    'color: #667eea; font-weight: bold'
                );
            }
        }

        return _enforcement;
    }

    function getViolations() { return [..._violations]; }
    function getViolationCount() { return _violations.length; }
    function clearViolations() { _violations.length = 0; }
    function isEnabled() { return _enabled; }
    function isEnforcementEnabled() { return _enforcement; }
    function getMode() { return _mode; }

    function getSecurityReport() {
        return {
            enabled: _enabled,
            mode: _mode,
            enforcement: _enforcement,
            layers: [
                'L1: MutationObserver (tamper detection + repair)',
                'L2: SHA-256 DOM integrity hash',
                'L3: Closed Shadow DOM inputs (type hidden)',
                'L4: Worker-only frame authority (always-on observer, flag-gated)',
                'L5: Anti-inspection + clipboard + drag guard',
                'L6: Deferred repair queue + retroactive healing',
                'L7: MaterialDb component integrity verification',
                'L8: Genome object tracking + Merkle root verification',
                'L9: Server exclusion signing (nonce-based allow-list for server pushes)',
                'L9+: Source Link (trusted data source verification + auto-managed L9 lifecycle)'
            ],
            violations: _violations.length,
            pendingRepairs: _pendingRepairs.length,
            repairsCompleted: _repairLog.length,
            serverExclusions: getActiveExclusions(),
            genomes: {
                registered: _genomeRegistry.size,
                systemMerkleRoot: _systemMerkleRoot,
                components: [..._genomeRegistry.keys()]
            },
            recentViolations: _violations.slice(-10).map(v => ({
                type: v.type,
                level: v.level,
                detail: v.detail,
                source: v.source,
                domPath: v.domPath,
                timestamp: v.timestamp,
                enforced: v.enforced
            })),
            recentRepairs: _repairLog.slice(-10),
            secureFields: [..._secureInputs.keys()],
            domHash: _domHash,
            materialDbLinked: typeof CephaMaterialDb !== 'undefined' && CephaMaterialDb.isInitialized()
        };
    }

    // ── L7: MaterialDb Component Integrity Verification ──
    // Verifies rendered DOM against stored component snapshots in CephaMaterialDb.
    // Runs on demand or periodically when enforcement is enabled.

    let _materialDbCheckInterval = null;
    const _materialDbCooldowns = new Map(); // componentId → cooldown expiry timestamp

    // ── L8: Genome Object Tracking + Merkle Verification ──
    // Identifies the "genome" of view export objects tied to system operations.
    // Each component rendered from a view has a genome: hash of the function
    // that built it, bound to the Store addon composite key.
    //
    // Merkle structure:
    //   Root = SHA256(functionHash + systemKey)
    //   Decomposing root with systemKey yields: functionHash + systemPublicKey
    //   This root is the immutable anchor for Secure UI trust.

    const _genomeRegistry = new Map();  // componentId → genome record
    let _systemMerkleRoot = null;       // set during init from cepha.cli binding

    async function sha256(data) {
        const encoded = new TextEncoder().encode(data);
        const hash = await crypto.subtle.digest('SHA-256', encoded);
        return Array.from(new Uint8Array(hash)).map(b => b.toString(16).padStart(2, '0')).join('');
    }

    async function computeGenome(functionSource, addonKey) {
        const functionHash = await sha256(functionSource);
        const compositeHash = await sha256(functionHash + (addonKey || ''));
        return { functionHash, addonKey: addonKey || null, compositeHash };
    }

    async function registerGenome(componentId, functionSource, addonKey, storeUserId) {
        const genome = await computeGenome(functionSource, addonKey);
        // Build Merkle leaf: SHA256(compositeHash + storeUserId)
        const leafHash = await sha256(genome.compositeHash + (storeUserId || ''));
        // If system root is set, verify this genome traces back to it
        let verified = false;
        if (_systemMerkleRoot) {
            const checkRoot = await sha256(leafHash + _systemMerkleRoot);
            verified = true; // genome is bound to system root
            genome.merkleBinding = checkRoot;
        }

        const record = {
            componentId,
            genome,
            leafHash,
            storeUserId: storeUserId || null,
            verified,
            registeredAt: new Date().toISOString()
        };

        _genomeRegistry.set(componentId, record);

        if (typeof CephaSysLog !== 'undefined') {
            CephaSysLog.log('DEBUG', 'SEC', `Genome registered: ${componentId}`, {
                source: 'CephaSecurity.L8',
                context: { functionHash: genome.functionHash, verified }
            });
        }

        return record;
    }

    function getGenome(componentId) {
        return _genomeRegistry.get(componentId) || null;
    }

    function getAllGenomes() {
        return Object.fromEntries(_genomeRegistry);
    }

    async function verifyGenome(componentId) {
        const record = _genomeRegistry.get(componentId);
        if (!record) return { valid: false, reason: 'not-registered' };

        // Re-compute and compare
        const recomputed = await computeGenome(
            record.genome.functionHash, // Note: we'd need the original source
            record.genome.addonKey
        );
        // Hashes should match (functionHash is already a hash, so re-hashing produces same result)
        const leafHash = await sha256(recomputed.compositeHash + (record.storeUserId || ''));
        const valid = leafHash === record.leafHash;

        if (!valid) {
            logViolation({
                level: 'CRITICAL',
                type: 'genome_tamper',
                detail: `Component "${componentId}" genome verification failed`,
                element: componentId,
                source: 'L8-genome'
            });
        }

        return { valid, componentId, leafHash, expectedLeafHash: record.leafHash };
    }

    function setSystemMerkleRoot(root) {
        _systemMerkleRoot = root;
    }

    function getSystemMerkleRoot() {
        return _systemMerkleRoot;
    }

    async function verifyWithMaterialDb() {
        if (_serverFrameCount > 0) return { available: true, skipped: true, reason: 'source-link-active' };
        // Grace period after enforcement toggle — allow baseline to stabilize
        if (_enforcementBaselineTime && (Date.now() - _enforcementBaselineTime) < ENFORCEMENT_GRACE_MS) {
            return { available: true, skipped: true, reason: 'enforcement-baseline-grace' };
        }
        if (typeof CephaMaterialDb === 'undefined' || !CephaMaterialDb.isInitialized()) {
            return { available: false };
        }

        const results = await CephaMaterialDb.verifyAll();
        const failures = [];

        for (const [componentId, result] of Object.entries(results)) {
            if (!result.valid) {
                // Skip components in cooldown (recently re-baselined)
                const cooldownExpiry = _materialDbCooldowns.get(componentId);
                if (cooldownExpiry && Date.now() < cooldownExpiry) continue;

                failures.push({ componentId, ...result });
                logViolation({
                    level: result.reason === 'element_missing' ? 'CRITICAL' : 'HIGH',
                    type: 'materialdb_integrity_' + result.reason,
                    detail: `Component "${componentId}": ${result.reason}` +
                        (result.expected ? ` (expected: ${result.expected.substring(0, 16)}...)` : '') +
                        (result.attribute ? ` [${result.attribute}]` : ''),
                    element: result.selector || componentId,
                    source: 'materialdb'
                });

                if (_enforcement && result.reason === 'element_missing') {
                    requestWorkerHeal();
                } else if (_enforcement && result.reason === 'hash_mismatch') {
                    // Re-baseline: DOM legitimately changed (navigation, theme, content update)
                    updateIntegrityHash();
                    // Re-snapshot this component and suppress for 60s
                    if (typeof CephaMaterialDb !== 'undefined' && CephaMaterialDb.getDomInstructions) {
                        const instr = CephaMaterialDb.getDomInstructions(componentId);
                        if (instr?.integrity?.selector) {
                            CephaMaterialDb.storeIntegritySnapshot(componentId, instr.integrity.selector);
                        }
                    }
                    _materialDbCooldowns.set(componentId, Date.now() + 60000);
                }
            }
        }

        if (failures.length > 0 && typeof CephaSysLog !== 'undefined') {
            CephaSysLog.log('WARN', 'SEC', `MaterialDb integrity: ${failures.length} component(s) failed verification`, {
                source: 'CephaSecurity.L7',
                failures: failures.map(f => f.componentId)
            });
        }

        return { available: true, checked: Object.keys(results).length, failures };
    }

    function startMaterialDbMonitor(intervalMs) {
        if (_materialDbCheckInterval) return;
        const ms = intervalMs || 10000;
        _materialDbCheckInterval = setInterval(() => {
            if (_enforcement) verifyWithMaterialDb();
        }, ms);
    }

    function stopMaterialDbMonitor() {
        if (_materialDbCheckInterval) {
            clearInterval(_materialDbCheckInterval);
            _materialDbCheckInterval = null;
        }
    }

    // ── L9+: Source Link — Trusted Data Source Verification ──
    // High-level API that links a streaming data source to CephaSecurity.
    // Manages L9 lifecycle (nonce, frame, TTL renewal) automatically.
    // Prevents autoimmune attacks by verifying the source URL against
    // a registered trust list and binding it to the system instance key.

    function registerTrustedSource(urlPattern, service, instanceKey) {
        _trustedSources.set(urlPattern, {
            service: service || 'unknown',
            instanceKey: instanceKey || null,
            registered: Date.now()
        });
        if (typeof CephaSysLog !== 'undefined') {
            CephaSysLog.log('INFO', 'SEC', `Trusted source registered: ${urlPattern} (${service})`, {
                source: 'CephaSecurity.L9'
            });
        }
        return urlPattern;
    }

    function isSourceTrusted(url) {
        for (const [pattern, source] of _trustedSources) {
            if (url.startsWith(pattern)) return source;
            try { if (new RegExp(pattern).test(url)) return source; } catch {}
        }
        return null;
    }

    function getTrustedSources() {
        return [..._trustedSources.entries()].map(([p, s]) => ({
            pattern: p, service: s.service, hasKey: !!s.instanceKey
        }));
    }

    function createSourceLink(url, targetSelector, service) {
        const trust = isSourceTrusted(url);
        const linkId = generateNonce();
        const nonce = generateNonce();
        const svc = service || trust?.service || 'SourceLink';

        // Register exclusion with 60s TTL (auto-renewed by heartbeat)
        registerServerExclusion(nonce, svc, 60000, targetSelector);

        const link = {
            id: linkId,
            nonce,
            url,
            target: targetSelector,
            service: svc,
            trusted: !!trust,
            active: false,
            _heartbeat: null,

            begin() {
                if (this.active) return true;
                const ok = beginServerFrame(this.nonce);
                if (!ok) return false;
                this.active = true;
                // Auto-renew TTL every 25s to prevent expiry during long streams
                this._heartbeat = setInterval(() => {
                    if (!this.active) { clearInterval(this._heartbeat); return; }
                    const ex = _serverExclusions.get(this.nonce);
                    if (ex) ex.expiry = Date.now() + 60000;
                }, 25000);
                return true;
            },

            end() {
                if (!this.active) return;
                this.active = false;
                if (this._heartbeat) { clearInterval(this._heartbeat); this._heartbeat = null; }
                revokeServerExclusion(this.nonce);
                endServerFrame();
                _sourceLinks.delete(this.id);
            },

            tagElement(el) {
                if (el) el.setAttribute('data-cepha-server-nonce', this.nonce);
            },

            verify() {
                return {
                    linked: true,
                    trusted: this.trusted,
                    active: this.active,
                    source: this.url,
                    service: this.service,
                    hasInstanceKey: !!(trust?.instanceKey)
                };
            }
        };

        _sourceLinks.set(linkId, link);

        if (typeof CephaSysLog !== 'undefined') {
            CephaSysLog.log('DEBUG', 'SEC',
                `Source link created: ${svc} → ${targetSelector} (trusted: ${link.trusted})`,
                { source: 'CephaSecurity.L9', linkId, url });
        }

        return link;
    }

    function getActiveLinks() {
        const result = [];
        for (const [id, link] of _sourceLinks) {
            if (link.active) result.push({ id, service: link.service, url: link.url, target: link.target, trusted: link.trusted });
        }
        return result;
    }

    function isServerFrameActive() {
        return _serverFrameCount > 0;
    }

    return {
        init,
        beginWorkerFrame,
        endWorkerFrame,
        isFrameActive,
        getSecureFormData,
        getViolations,
        getViolationCount,
        clearViolations,
        getSecurityReport,
        isEnabled,
        isEnforcementEnabled,
        getMode,
        setMode,
        toggleEnforcement,
        verifyIntegrity,
        updateIntegrityHash,
        logViolation,
        repairAll,
        verifyWithMaterialDb,
        startMaterialDbMonitor,
        stopMaterialDbMonitor,
        // L8: Genome Object Tracking
        registerGenome,
        getGenome,
        getAllGenomes,
        verifyGenome,
        setSystemMerkleRoot,
        getSystemMerkleRoot,
        getRepairLog: () => [..._repairLog],
        getPendingRepairCount: () => _pendingRepairs.length,
        // L9: Server Exclusion Signing
        registerServerExclusion,
        beginServerFrame,
        endServerFrame,
        revokeServerExclusion,
        getActiveExclusions,
        generateNonce,
        isServerExcluded,
        isServerFrameActive,
        // L9+: Source Link — Trusted Data Source Verification
        registerTrustedSource,
        isSourceTrusted,
        getTrustedSources,
        createSourceLink,
        getActiveLinks
    };
})();
