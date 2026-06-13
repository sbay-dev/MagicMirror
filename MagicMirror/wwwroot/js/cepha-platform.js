// ═══════════════════════════════════════════════════════════════════════════════
// 🧬 Cepha Platform — OS Detection & Native Interactions
// ─────────────────────────────────────────────────────────────────────────────
// Auto-detects the user's operating system and sets `data-platform` on <html>
// so CMUI-Mob applies the correct native styles.
//
// Interactions implemented from spec:
//   Android → MD3 Ripple from real touch coordinates (m3.material.io)
//   iOS     → Press-dim + spring scale (Apple HIG)
//
// This script runs once at page load and is idempotent.
// MAUI hosts can skip this — they set data-platform from C#.
// ═══════════════════════════════════════════════════════════════════════════════

(function () {
    'use strict';

    // ── Platform Detection ──────────────────────────────────────────────────

    function detectPlatform() {
        var ua = navigator.userAgent || '';

        // Check userAgentData (modern Chromium)
        if (navigator.userAgentData && navigator.userAgentData.platform) {
            var p = navigator.userAgentData.platform.toLowerCase();
            if (p === 'android') return 'android';
            if (p === 'ios') return 'ios';
            if (p === 'windows') return 'windows';
            if (p === 'macos' || p === 'chromeos') return p === 'chromeos' ? 'android' : 'macos';
            if (p === 'linux') return 'linux';
        }

        // Mobile first
        if (/Android/i.test(ua)) return 'android';
        if (/iPhone|iPad|iPod/i.test(ua)) return 'ios';
        if (/CrOS/i.test(ua)) return 'android'; // ChromeOS → Material

        // Desktop
        if (/Windows/i.test(ua)) return 'windows';
        if (/Macintosh|Mac OS X/i.test(ua)) return 'macos';
        if (/Linux|X11/i.test(ua)) return 'linux';

        return 'default';
    }

    // ── Apply Platform Attribute ────────────────────────────────────────────

    var detected = detectPlatform();
    var html = document.documentElement;

    // Don't override if already set (e.g., by MAUI C# code or server)
    if (!html.hasAttribute('data-platform')) {
        html.setAttribute('data-platform', detected);
    }

    // ── Public API ──────────────────────────────────────────────────────────

    window.CephaPlatform = {
        detected: detected,
        current: function () { return html.getAttribute('data-platform'); },
        isNative: function () { return this.current() !== 'default'; },
        isMobile: function () {
            var p = this.current();
            return p === 'android' || p === 'ios';
        },

        /** Override platform (for testing/preview) */
        set: function (platform) {
            html.setAttribute('data-platform', platform);
        },

        /** Reset to auto-detected value */
        reset: function () {
            html.setAttribute('data-platform', this.detected);
        }
    };


    // ╔═══════════════════════════════════════════════════════════════════════╗
    // ║  MD3 RIPPLE — Spec-Accurate Material Design 3 Touch Ripple          ║
    // ║  Source: m3.material.io/foundations/interaction/states               ║
    // ║                                                                      ║
    // ║  Behavior:                                                           ║
    // ║  1. On pointerdown: calculate touch point relative to button         ║
    // ║  2. Compute max radius to farthest corner                            ║
    // ║  3. Create circle at touch point, expand linearly over 550ms         ║
    // ║  4. State layer opacity → 0.10 (pressed)                            ║
    // ║  5. On pointerup: fade ripple over 150ms                            ║
    // ║  6. State layer opacity → 0 (or 0.08 if still hovered)              ║
    // ╚═══════════════════════════════════════════════════════════════════════╝

    if (detected === 'android' || html.getAttribute('data-platform') === 'android') {

        // Distance from point to farthest corner of rectangle
        function maxRadius(x, y, w, h) {
            var dx = Math.max(x, w - x);
            var dy = Math.max(y, h - y);
            return Math.sqrt(dx * dx + dy * dy);
        }

        document.addEventListener('pointerdown', function (e) {
            var btn = e.target.closest('.cepha-btn, .md3-btn');
            if (!btn || btn.disabled) return;

            var rect = btn.getBoundingClientRect();

            // Touch coordinates relative to button
            var x = e.clientX - rect.left;
            var y = e.clientY - rect.top;

            // Radius to reach the farthest corner
            var radius = maxRadius(x, y, rect.width, rect.height);
            var diameter = radius * 2;

            // Create ripple element
            var ripple = document.createElement('span');
            ripple.className = 'md3-ripple-wave md3-ripple-growing';

            // Get the on-color for this button's container
            var style = getComputedStyle(btn);
            var color = style.color;

            ripple.style.cssText =
                'width:' + diameter + 'px;' +
                'height:' + diameter + 'px;' +
                'left:' + (x - radius) + 'px;' +
                'top:' + (y - radius) + 'px;' +
                'background:' + color + ';' +
                'opacity:0.10;';

            btn.appendChild(ripple);

            // Track timing for proper fade
            var startTime = Date.now();
            var minGrowTime = 225; // ms — minimum visible grow before allowing fade

            function fadeRipple() {
                var elapsed = Date.now() - startTime;

                if (elapsed < minGrowTime) {
                    // Wait for minimum grow time
                    setTimeout(function () {
                        startFade();
                    }, minGrowTime - elapsed);
                } else {
                    startFade();
                }
            }

            function startFade() {
                ripple.classList.remove('md3-ripple-growing');
                ripple.classList.add('md3-ripple-fading');

                ripple.addEventListener('animationend', function () {
                    if (ripple.parentNode) ripple.remove();
                });

                // Safety cleanup
                setTimeout(function () {
                    if (ripple.parentNode) ripple.remove();
                }, 300);
            }

            // Listen for pointer release
            function onUp() {
                fadeRipple();
                document.removeEventListener('pointerup', onUp);
                document.removeEventListener('pointercancel', onUp);
            }

            document.addEventListener('pointerup', onUp);
            document.addEventListener('pointercancel', onUp);

            // Safety: if no pointerup within 2s, force fade
            setTimeout(function () {
                document.removeEventListener('pointerup', onUp);
                document.removeEventListener('pointercancel', onUp);
                if (ripple.parentNode && !ripple.classList.contains('md3-ripple-fading')) {
                    startFade();
                }
            }, 2000);
        });
    }


    // ╔═══════════════════════════════════════════════════════════════════════╗
    // ║  iOS PRESS FEEDBACK — Haptic-like visual response                    ║
    // ║  iOS uses CSS :active (opacity + scale) — no JS ripple needed.       ║
    // ║  This section adds touchstart/end for faster feedback on mobile      ║
    // ║  since :active has 300ms delay on iOS Safari.                        ║
    // ╚═══════════════════════════════════════════════════════════════════════╝

    if (detected === 'ios' || html.getAttribute('data-platform') === 'ios') {

        document.addEventListener('touchstart', function (e) {
            var btn = e.target.closest('.cepha-btn, .hig-btn');
            if (!btn || btn.disabled) return;
            btn.classList.add('hig-pressing');
        }, { passive: true });

        document.addEventListener('touchend', function () {
            var pressing = document.querySelectorAll('.hig-pressing');
            for (var i = 0; i < pressing.length; i++) {
                pressing[i].classList.remove('hig-pressing');
            }
        }, { passive: true });

        document.addEventListener('touchcancel', function () {
            var pressing = document.querySelectorAll('.hig-pressing');
            for (var i = 0; i < pressing.length; i++) {
                pressing[i].classList.remove('hig-pressing');
            }
        }, { passive: true });

        // Inject iOS press class rule
        var iosStyle = document.createElement('style');
        iosStyle.textContent =
            '.hig-pressing, [data-platform="ios"] .hig-pressing {' +
            '  opacity: 0.7 !important;' +
            '  transform: scale(0.98) !important;' +
            '  transition-duration: 100ms !important;' +
            '}';
        document.head.appendChild(iosStyle);
    }


    // ── Keyboard Focus Management ───────────────────────────────────────────
    // Show focus rings only for keyboard navigation (all platforms)

    var lastInputWasKeyboard = false;

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Tab') lastInputWasKeyboard = true;
    });

    document.addEventListener('pointerdown', function () {
        lastInputWasKeyboard = false;
    });

    document.addEventListener('focusin', function (e) {
        if (!lastInputWasKeyboard && e.target.classList &&
            (e.target.classList.contains('cepha-btn') ||
             e.target.classList.contains('md3-btn') ||
             e.target.classList.contains('hig-btn'))) {
            e.target.blur();
        }
    });

    // ── Console Announcement ────────────────────────────────────────────────

    if (typeof console !== 'undefined' && console.log) {
        var platform = html.getAttribute('data-platform');
        var label = platform === 'android' ? 'Material Design 3' :
                    platform === 'ios'     ? 'Apple HIG' :
                    platform === 'windows' ? 'Fluent' :
                    platform === 'macos'   ? 'AppKit' :
                    platform === 'linux'   ? 'GTK/Adwaita' : 'Default';
        console.log(
            '%c🧬 CMUI-Mob%c ' + platform + ' → ' + label,
            'color:#667eea;font-weight:700',
            'color:inherit'
        );
    }
})();
