// 🧬 Cepha Material UI — Component Runtime with Advanced Listening
// Provides Material Design components + real-time streaming guards.
//
// Features:
//   - Ripple effects, snackbar, dialog, drawer management
//   - Form data collection from secure + regular inputs
//   - Streaming listener — continuous DOM diffing from Worker frames
//   - MutationObserver-based live patching (only changed nodes update)
//   - Security event bus — all components report to CephaSecurity

const CephaMaterial = (() => {
    'use strict';

    let _worker = null;
    let _streamInterval = null;
    let _lastFrameHash = null;
    let _streamCooldown = 0;
    let _listeners = new Map();     // eventName → Set<callback>
    let _snackbarTimer = null;

    // ── Initialization ──

    function init(workerRef) {
        _worker = workerRef;
        initRipples();
        initDialogs();
        initDrawers();
        initTabs();
        initForms();
        initThemeToggle();
        console.log('%c🧬 CephaMaterial: Ready', 'color: #667eea; font-weight: bold');
    }

    // ── Ripple Effect ──

    function initRipples() {
        document.addEventListener('pointerdown', (e) => {
            const btn = e.target.closest('.cepha-btn');
            if (!btn) return;

            const rect = btn.getBoundingClientRect();
            const size = Math.max(rect.width, rect.height);
            const x = e.clientX - rect.left - size / 2;
            const y = e.clientY - rect.top - size / 2;

            const ripple = document.createElement('span');
            ripple.className = 'cepha-ripple';
            ripple.style.width = ripple.style.height = size + 'px';
            ripple.style.left = x + 'px';
            ripple.style.top = y + 'px';
            btn.appendChild(ripple);

            ripple.addEventListener('animationend', () => ripple.remove());
        });
    }

    // ── Dialogs ──

    function initDialogs() {
        document.addEventListener('click', (e) => {
            const trigger = e.target.closest('[data-cepha-dialog]');
            if (trigger) {
                const id = trigger.getAttribute('data-cepha-dialog');
                openDialog(id);
                return;
            }

            const close = e.target.closest('[data-cepha-dialog-close]');
            if (close) {
                const overlay = close.closest('.cepha-dialog-overlay');
                if (overlay) closeDialog(overlay.id);
                return;
            }

            // Close on overlay click
            if (e.target.classList.contains('cepha-dialog-overlay') &&
                e.target.classList.contains('open')) {
                closeDialog(e.target.id);
            }
        });

        // ESC to close topmost dialog
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                const open = document.querySelector('.cepha-dialog-overlay.open');
                if (open) closeDialog(open.id);
            }
        });
    }

    function openDialog(id) {
        const el = document.getElementById(id);
        if (!el) return;
        el.classList.add('open');
        document.body.style.overflow = 'hidden';
        emit('dialog:open', { id });

        // Focus trap
        const focusable = el.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
        if (focusable.length) focusable[0].focus();
    }

    function closeDialog(id) {
        const el = document.getElementById(id);
        if (!el) return;
        el.classList.remove('open');
        document.body.style.overflow = '';
        emit('dialog:close', { id });
    }

    // ── Drawers ──

    function initDrawers() {
        document.addEventListener('click', (e) => {
            const trigger = e.target.closest('[data-cepha-drawer]');
            if (trigger) {
                toggleDrawer(trigger.getAttribute('data-cepha-drawer'));
                return;
            }

            if (e.target.classList.contains('cepha-drawer-overlay') &&
                e.target.classList.contains('visible')) {
                const drawer = document.querySelector('.cepha-drawer.open');
                if (drawer) toggleDrawer(drawer.id);
            }
        });
    }

    function toggleDrawer(id) {
        const drawer = document.getElementById(id);
        if (!drawer) return;

        const isOpen = drawer.classList.toggle('open');
        const overlay = drawer.nextElementSibling;
        if (overlay?.classList.contains('cepha-drawer-overlay')) {
            overlay.classList.toggle('visible', isOpen);
        }
        emit('drawer:toggle', { id, open: isOpen });
    }

    // ── Tabs ──

    function initTabs() {
        document.addEventListener('click', (e) => {
            const tab = e.target.closest('.cepha-tab');
            if (!tab) return;

            const tabGroup = tab.closest('.cepha-tabs');
            if (!tabGroup) return;

            // Deactivate all
            tabGroup.querySelectorAll('.cepha-tab').forEach(t => t.classList.remove('active'));
            tab.classList.add('active');

            // Show corresponding panel
            const target = tab.getAttribute('data-cepha-tab-target');
            if (target) {
                const panel = document.getElementById(target);
                if (panel) {
                    panel.parentElement.querySelectorAll('[data-cepha-tab-panel]')
                        .forEach(p => p.hidden = true);
                    panel.hidden = false;
                }
            }

            emit('tab:change', { target, tab: tab.textContent.trim() });
        });
    }

    // ── Forms (with Secure Field integration) ──

    function initForms() {
        document.addEventListener('submit', (e) => {
            const form = e.target.closest('form[data-cepha-form]');
            if (!form) return;

            e.preventDefault();

            // Collect regular form data
            const formData = new FormData(form);
            const data = Object.fromEntries(formData);

            // Merge secure field values
            if (typeof CephaSecurity !== 'undefined') {
                const secureData = CephaSecurity.getSecureFormData();
                Object.assign(data, secureData);
            }

            const action = form.getAttribute('data-cepha-action') || form.action;
            const method = form.getAttribute('data-cepha-method') || form.method || 'POST';

            emit('form:submit', { action, method, data });

            // Send to Worker for server-side processing
            if (_worker) {
                _worker.postMessage({
                    type: 'form-submit',
                    action,
                    method: method.toUpperCase(),
                    data
                });
            }
        });
    }

    // ── Theme Toggle ──

    function initThemeToggle() {
        // Restore saved preference
        const saved = localStorage.getItem('cepha-theme');
        if (saved) {
            document.documentElement.setAttribute('data-theme', saved);
        }
        // Clear any stale inline CSS custom properties
        document.documentElement.style.cssText = '';

        // Use EVENT DELEGATION — survives Worker frame DOM replacements
        document.addEventListener('click', (e) => {
            const btn = e.target.closest('#cepha-theme-toggle');
            if (btn) toggleTheme();
        });

        // Sync button icon on init
        _syncThemeButtonIcon();
    }

    function _syncThemeButtonIcon() {
        const btn = document.getElementById('cepha-theme-toggle');
        if (!btn) return;
        const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
        btn.textContent = isDark ? '☀️' : '🌗';
    }

    function toggleTheme() {
        const html = document.documentElement;
        const isDark = html.getAttribute('data-theme') === 'dark';
        const next = isDark ? '' : 'dark';
        html.setAttribute('data-theme', next);
        try { localStorage.setItem('cepha-theme', next); } catch (e) {}
        // Clear any inline CSS custom properties so [data-theme] rules take effect
        html.style.cssText = '';
        // Sync button icon
        _syncThemeButtonIcon();
        // Sync MaterialDb state (without re-applying inline tokens)
        if (typeof CephaMaterialDb !== 'undefined' && CephaMaterialDb.isInitialized && CephaMaterialDb.isInitialized()) {
            CephaMaterialDb.syncThemeState(next || 'light');
        }
        // Update stream hash to prevent false desync
        notifyDomUpdate();
        emit('theme:changed', { theme: next || 'light' });
    }

    // ── Snackbar ──

    function showSnackbar(message, duration = 4000) {
        let snackbar = document.querySelector('.cepha-snackbar');
        if (!snackbar) {
            snackbar = document.createElement('div');
            snackbar.className = 'cepha-snackbar';
            document.body.appendChild(snackbar);
        }

        clearTimeout(_snackbarTimer);
        snackbar.textContent = message;
        snackbar.classList.add('show');

        _snackbarTimer = setTimeout(() => {
            snackbar.classList.remove('show');
        }, duration);
    }

    // ── Streaming Listener — Advanced Real-Time DOM Monitoring ──
    //
    // This is the core "listening" capability. It runs a continuous
    // verification loop that:
    //   1. Computes a hash of the current view container
    //   2. Compares against the last known-good hash from Worker frame
    //   3. If mismatch detected outside of Worker frame → triggers alert
    //   4. Optionally requests a full re-render from Worker (self-healing)

    function startStreamListener(containerSelector = '#app', intervalMs = 500) {
        if (_streamInterval) clearInterval(_streamInterval);

        const container = document.querySelector(containerSelector);
        if (!container) return;

        _streamInterval = setInterval(async () => {
            // Skip during cooldown, active Worker frame, or when security is not enabled
            if (_streamCooldown > Date.now()) return;
            if (typeof CephaSecurity !== 'undefined') {
                if (!CephaSecurity.isEnabled()) return;
                if (CephaSecurity.isFrameActive()) return;
            }

            const current = await quickHash(container.innerHTML);

            if (_lastFrameHash && current !== _lastFrameHash) {
                // Cooldown: suppress repeated desync alerts (30s)
                _streamCooldown = Date.now() + 30000;

                if (typeof CephaSecurity !== 'undefined') {
                    CephaSecurity.logViolation({
                        level: 'HIGH',
                        type: 'stream_desync',
                        detail: 'DOM state diverged from Worker stream',
                        element: containerSelector
                    });

                    if (CephaSecurity.isEnforcementEnabled() && _worker) {
                        _worker.postMessage({ type: 'rerender', path: location.pathname });
                    }
                }

                _lastFrameHash = current;
            }
        }, intervalMs);

        emit('stream:started', { interval: intervalMs });
    }

    function stopStreamListener() {
        if (_streamInterval) {
            clearInterval(_streamInterval);
            _streamInterval = null;
        }
        emit('stream:stopped', {});
    }

    // Called after each successful Worker frame to update the known-good hash
    async function onWorkerFrameApplied(container) {
        _lastFrameHash = await quickHash(container.innerHTML);
        // Re-sync theme toggle button icon (button DOM element is replaced each frame)
        _syncThemeButtonIcon();
    }

    // Allow internal scripts to update the hash without triggering desync
    async function notifyDomUpdate() {
        const container = document.querySelector('#app');
        if (container) {
            _lastFrameHash = await quickHash(container.innerHTML);
        }
    }

    // Fast hash using SubtleCrypto
    async function quickHash(text) {
        const data = new TextEncoder().encode(text);
        const hash = await crypto.subtle.digest('SHA-256', data);
        // Use first 8 bytes for speed (64-bit fingerprint)
        return Array.from(new Uint8Array(hash).slice(0, 8))
            .map(b => b.toString(16).padStart(2, '0')).join('');
    }

    // ── Event Bus (Advanced Listening) ──
    //
    // All Material components and security events flow through this bus.
    // External code can subscribe to events like:
    //   CephaMaterial.on('form:submit', data => { ... })
    //   CephaMaterial.on('stream:tamper', data => { ... })
    //   CephaMaterial.on('security:violation', data => { ... })

    function on(event, callback) {
        if (!_listeners.has(event)) _listeners.set(event, new Set());
        _listeners.get(event).add(callback);
        return () => off(event, callback); // return unsubscribe function
    }

    function off(event, callback) {
        _listeners.get(event)?.delete(callback);
    }

    function emit(event, data) {
        const handlers = _listeners.get(event);
        if (!handlers) return;
        for (const fn of handlers) {
            try { fn(data); } catch (e) { console.error(`[CephaMaterial] Event error (${event}):`, e); }
        }
    }

    // ── Navigation Listener ──
    // Monitors SPA navigation and verifies the view integrity after each route change

    function initNavigationListener() {
        on('route:change', async (data) => {
            // After route change, wait for next frame then verify
            requestAnimationFrame(async () => {
                const container = document.querySelector('#app');
                if (container) {
                    await onWorkerFrameApplied(container);
                }
            });
        });
    }

    // ── Security Dashboard Widget ──
    // Small floating badge showing security status

    function createSecurityIndicator() {
        const badge = document.createElement('div');
        badge.className = 'cepha-security-badge secure';
        badge.setAttribute('data-cepha-secure-skip', 'true');
        badge.style.cssText = 'position:fixed;bottom:16px;right:16px;z-index:9999;cursor:pointer;';
        badge.innerHTML = '<span>SECURE</span>';

        // Click: show report + toggle enforcement in dev mode
        badge.addEventListener('click', () => {
            if (typeof CephaSecurity !== 'undefined') {
                const report = CephaSecurity.getSecurityReport();
                console.table(report.recentViolations);
                console.log('Full report:', report);

                // Show SysLog anomaly summary in console
                if (typeof CephaSysLog !== 'undefined' && CephaSysLog.isInitialized()) {
                    const summary = CephaSysLog.getAnomalySummary();
                    console.log('📋 SysLog Anomaly Summary:', summary);
                }

                if (report.mode === 'development') {
                    const enforced = CephaSecurity.toggleEnforcement();
                    showSnackbar(
                        enforced
                            ? '🛡️ Enforcement ON — violations will revert DOM changes'
                            : '🛡️ Enforcement OFF — violations logged only'
                    );
                } else {
                    showSnackbar(`🛡️ ${report.violations} violations | Production mode (always enforced)`);
                }
            }
        });

        document.body.appendChild(badge);

        // Update badge state based on violations
        setInterval(() => {
            if (typeof CephaSecurity === 'undefined') return;
            const count = CephaSecurity.getViolationCount();
            const mode = CephaSecurity.getMode();
            const enforced = CephaSecurity.isEnforcementEnabled();

            if (count === 0) {
                badge.className = 'cepha-security-badge secure';
                badge.querySelector('span').textContent = enforced ? '🔒 SECURE' : '🛡️ SECURE';
            } else if (count < 5) {
                badge.className = 'cepha-security-badge warning';
                badge.querySelector('span').textContent = `${count} ALERT${count > 1 ? 'S' : ''}`;
            } else {
                badge.className = 'cepha-security-badge breach';
                badge.querySelector('span').textContent = `${count} VIOLATION${count > 1 ? 'S' : ''}`;
            }
        }, 2000);
    }

    // ── Public API ──

    return {
        init,
        openDialog,
        closeDialog,
        toggleDrawer,
        toggleTheme,
        showSnackbar,
        startStreamListener,
        stopStreamListener,
        onWorkerFrameApplied,
        notifyDomUpdate,
        createSecurityIndicator,
        on,
        off,
        emit
    };
})();
