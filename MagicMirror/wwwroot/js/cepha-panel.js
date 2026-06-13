// 🧬 CephaPanel — Intelligent Surface Web Component
// A smart panel that elements are placed on. Has Cepha card aesthetics,
// luminance-based color intelligence, and L8 genome binding.
//
// No global listeners. Works economically by reading its own computed
// style to understand the background and ensure text visibility.
//
// Usage:
//   <cepha-panel>
//     <h3>Title</h3>
//     <p>Content is always readable regardless of theme.</p>
//   </cepha-panel>
//
//   <cepha-panel variant="elevated" padding="lg">
//     <table>...</table>
//   </cepha-panel>
//
//   <cepha-panel variant="chat" dir="rtl">
//     Chat bubbles go here
//   </cepha-panel>

(() => {
  'use strict';

  if (customElements.get('cepha-panel')) return;

  // Relative luminance (WCAG 2.1) from sRGB
  function luminance(r, g, b) {
    const [rs, gs, bs] = [r, g, b].map(c => {
      c /= 255;
      return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * rs + 0.7152 * gs + 0.0722 * bs;
  }

  // Parse CSS color string to [r, g, b, a]
  function parseColor(str) {
    if (!str || str === 'transparent' || str === 'rgba(0, 0, 0, 0)') return null;
    const m = str.match(/rgba?\(\s*(\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/);
    if (m) return [+m[1], +m[2], +m[3], m[4] !== undefined ? +m[4] : 1];
    return null;
  }

  // Walk up the DOM to find the effective opaque background
  function resolveBackground(el) {
    let node = el;
    while (node && node !== document.documentElement) {
      const bg = getComputedStyle(node).backgroundColor;
      const c = parseColor(bg);
      if (c && c[3] > 0.5) return c; // Found a substantially opaque background
      node = node.parentElement;
    }
    // Fallback: check html element or assume white
    const htmlBg = getComputedStyle(document.documentElement).backgroundColor;
    return parseColor(htmlBg) || [255, 255, 255, 1];
  }

  // Determine if background is dark (luminance < 0.5)
  function isDark(rgba) {
    return luminance(rgba[0], rgba[1], rgba[2]) < 0.35;
  }

  // VARIANT presets
  const VARIANTS = {
    default: { elevated: false, blur: false },
    elevated: { elevated: true, blur: false },
    chat: { elevated: false, blur: false },
    glass: { elevated: true, blur: true }
  };

  // PADDING presets
  const PADDINGS = {
    none: '0',
    xs: 'var(--cepha-space-xs, 4px)',
    sm: 'var(--cepha-space-sm, 8px)',
    md: 'var(--cepha-space-md, 16px)',
    lg: 'var(--cepha-space-lg, 24px)',
    xl: 'var(--cepha-space-xl, 32px)'
  };

  class CephaPanel extends HTMLElement {
    #shadow;
    #container;
    #genomeRecord = null;

    static get observedAttributes() {
      return ['variant', 'padding', 'radius', 'genome-id'];
    }

    constructor() {
      super();
      this.#shadow = this.attachShadow({ mode: 'open' });
    }

    connectedCallback() {
      this.#render();
      this.#adaptColors();
      this.#registerGenome();
    }

    attributeChangedCallback() {
      if (this.#container) {
        this.#render();
        this.#adaptColors();
      }
    }

    #render() {
      const variant = VARIANTS[this.getAttribute('variant')] || VARIANTS.default;
      const padding = PADDINGS[this.getAttribute('padding')] || PADDINGS.md;
      const radius = this.getAttribute('radius') || 'var(--cepha-radius-lg, 12px)';

      this.#shadow.innerHTML = '';

      const style = document.createElement('style');
      style.textContent = `
        :host {
          display: block;
          contain: layout style;
        }

        .cp-surface {
          position: relative;
          padding: ${padding};
          border-radius: ${radius};
          border: 1px solid var(--cp-border, var(--cepha-border, #e2e8f0));
          background: var(--cp-bg, var(--cepha-surface-elevated, #fff));
          color: var(--cp-text, var(--cepha-text, #1a202c));
          box-shadow: ${variant.elevated
            ? 'var(--cepha-shadow-lg, 0 10px 15px rgba(0,0,0,0.1))'
            : 'var(--cepha-shadow, 0 1px 3px rgba(0,0,0,0.1))'};
          transition: box-shadow 0.2s cubic-bezier(0.4, 0, 0.2, 1),
                      background 0.3s ease,
                      border-color 0.3s ease,
                      color 0.3s ease;
          overflow: hidden;
          font-family: var(--cepha-font, 'Inter', sans-serif);
          line-height: 1.6;
          ${variant.blur ? 'backdrop-filter: blur(12px); -webkit-backdrop-filter: blur(12px);' : ''}
        }

        .cp-surface:hover {
          box-shadow: ${variant.elevated
            ? 'var(--cepha-shadow-xl, 0 20px 25px rgba(0,0,0,0.1))'
            : 'var(--cepha-shadow-md, 0 4px 6px rgba(0,0,0,0.07))'};
        }

        /* Color-adapted text classes set via JS */
        :host([data-cp-dark]) .cp-surface {
          --cp-bg: var(--cepha-surface-elevated, #242b3d);
          --cp-text: var(--cepha-text, #e8edf5);
          --cp-border: var(--cepha-border, #2a3244);
          --cp-text-secondary: var(--cepha-text-secondary, #8892a4);
        }

        :host([data-cp-light]) .cp-surface {
          --cp-bg: var(--cepha-surface-elevated, #fff);
          --cp-text: #1a202c;
          --cp-border: var(--cepha-border, #e2e8f0);
          --cp-text-secondary: #718096;
        }

        /* Slotted content inherits color intelligence */
        ::slotted(*) {
          color: inherit;
        }

        ::slotted(code), ::slotted(pre) {
          font-family: var(--cepha-font-mono, 'JetBrains Mono', monospace);
          font-size: 0.9em;
        }

        ::slotted(table) {
          width: 100%;
          border-collapse: collapse;
        }

        ::slotted(th), ::slotted(td) {
          padding: var(--cepha-space-sm, 8px) var(--cepha-space-md, 16px);
          border-bottom: 1px solid var(--cp-border, var(--cepha-divider, #edf2f7));
          text-align: start;
        }
      `;

      this.#container = document.createElement('div');
      this.#container.className = 'cp-surface';
      this.#container.setAttribute('data-cepha-secure-skip', '');

      const slot = document.createElement('slot');
      this.#container.appendChild(slot);

      this.#shadow.appendChild(style);
      this.#shadow.appendChild(this.#container);
    }

    #adaptColors() {
      // Read the effective background color from the hosting context
      const bg = resolveBackground(this);
      const dark = isDark(bg);

      // Set the polarity attribute — CSS handles the rest
      if (dark) {
        this.setAttribute('data-cp-dark', '');
        this.removeAttribute('data-cp-light');
      } else {
        this.setAttribute('data-cp-light', '');
        this.removeAttribute('data-cp-dark');
      }
    }

    async #registerGenome() {
      const genomeId = this.getAttribute('genome-id');
      if (!genomeId) return;

      if (typeof CephaSecurity !== 'undefined' && CephaSecurity.registerGenome) {
        // Hash the component's render function as its genome fingerprint
        const source = this.#render.toString() + '|' + (this.getAttribute('variant') || 'default');
        try {
          this.#genomeRecord = await CephaSecurity.registerGenome(
            'cepha-panel:' + genomeId,
            source,
            this.getAttribute('variant') || 'default'
          );
        } catch (_) { /* L8 optional */ }
      }
    }

    // Public API
    getGenome() { return this.#genomeRecord; }
    refreshColors() { this.#adaptColors(); }
  }

  customElements.define('cepha-panel', CephaPanel);
})();
