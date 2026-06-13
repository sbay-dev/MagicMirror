// 🌊 CephaStream — Scoped Streaming for LLM Model Interfaces
// Each use case (Chat, Sarmad, floating panel) gets an isolated scope
// with its own AbortController, state, and L9+ Source Link lifecycle.
//
// Why: Prevents queue buildup from slow remote providers. Each scope
// can cancel its own in-flight request without affecting others.
// Protects the app from cascading failures in provider queues.

const CephaStream = (() => {
  'use strict';

  const DEFAULT_TIMEOUT = 120000; // 2 minutes max stream duration

  /**
   * Create an isolated streaming scope for a chat/LLM interface.
   * @param {Object} config
   * @param {string} config.service  — Name for L9+ logging ('StreamingChat', 'Sarmad', etc.)
   * @param {string} config.target   — CSS selector for L9+ exclusion zone ('#chat-messages')
   * @param {number} [config.timeout] — Max stream duration in ms (default: 120000)
   * @returns {CephaStreamScope}
   */
  function createScope(config) {
    const service = config.service || 'CephaStream';
    const target = config.target || 'body';
    const timeout = config.timeout || DEFAULT_TIMEOUT;

    let _controller = null;
    let _link = null;
    let _streaming = false;
    let _timeoutId = null;
    let _pendingTags = []; // Elements tagged before _link is created

    /**
     * Start a streaming request. Cancels any previous in-flight request.
     * @param {string} url — Full streaming endpoint URL
     * @param {Object} callbacks
     * @param {function(string)} callbacks.onChar — Called per character/token
     * @param {function()} [callbacks.onDone] — Called when stream completes
     * @param {function(Error)} [callbacks.onError] — Called on error (not abort)
     * @param {function()} [callbacks.onAbort] — Called when cancelled
     * @returns {Promise<void>}
     */
    async function stream(url, callbacks) {
      const { onChar, onDone, onError, onAbort } = callbacks;

      // Cancel any previous in-flight request in this scope
      abort();

      _controller = new AbortController();
      _streaming = true;

      // Safety timeout — prevent infinite hangs from unresponsive providers
      _timeoutId = setTimeout(() => {
        if (_controller) _controller.abort();
      }, timeout);

      // L9+ Source Link — auto-managed security exclusion
      if (typeof CephaSecurity !== 'undefined' && CephaSecurity.createSourceLink) {
        _link = CephaSecurity.createSourceLink(url, target, service);
        // Apply any elements tagged before _link was created
        for (const el of _pendingTags) _link.tagElement(el);
        _pendingTags = [];
        _link.begin();
      }

      try {
        const resp = await fetch(url, { signal: _controller.signal });
        if (!resp.ok) throw new Error('Provider returned ' + resp.status);

        const reader = resp.body.getReader();
        const decoder = new TextDecoder();
        let buf = '';

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buf += decoder.decode(value, { stream: true });
          const parts = buf.split('\n');
          buf = parts.pop() || '';

          for (let i = 0; i < parts.length; i++) {
            const line = parts[i].trim();
            if (!line) continue;
            try {
              const d = JSON.parse(line);
              if (d.type === 'char' && onChar) onChar(d.value);
              if (d.type === 'done' && onDone) onDone();
            } catch (_) { /* skip malformed lines */ }
          }
        }

        if (onDone) onDone();
      } catch (err) {
        if (err.name === 'AbortError') {
          if (onAbort) onAbort();
        } else {
          if (onError) onError(err);
        }
      } finally {
        _cleanup();
      }
    }

    function abort() {
      if (_controller) {
        _controller.abort();
        _controller = null;
      }
      _cleanup();
    }

    function _cleanup() {
      if (_timeoutId) { clearTimeout(_timeoutId); _timeoutId = null; }
      if (_link) { _link.end(); _link = null; }
      _streaming = false;
    }

    /**
     * Tag a DOM element with this scope's L9+ nonce for security bypass.
     * @param {HTMLElement} el
     */
    function tagElement(el) {
      if (_link && el) _link.tagElement(el);
      else if (el) _pendingTags.push(el);
    }

    return {
      stream,
      abort,
      tagElement,
      get streaming() { return _streaming; },
      get service() { return service; },
      get hasActiveLink() { return _link?.active ?? false; }
    };
  }

  return { createScope };
})();
