/**
 * 🤖 CephaSarmad — سرمد يراقب (Sarmad Watches)
 * AI Security Log Guardian for CephaSecurity & CephaSysLog
 *
 * Connects to Ollama via proxy, analyzes security events in real-time,
 * and provides contextual AI explanations to the user.
 * Uses L9 Server Exclusion Signing for safe DOM updates.
 */
(function() {
  'use strict';

  var SARMAD_VERSION = '1.0.0';
  var SYSTEM_PROMPT = [
    'أنت سرمد، حارس السجلات الأمنية في نظام سيفا (Cepha).',
    'You are Sarmad (سرمد), the security log guardian for the Cepha WASM MVC runtime.',
    'Your role: analyze CephaSecurity events and explain them to developers in clear, actionable terms.',
    '',
    '## Architecture You Guard:',
    '- Cepha runs ASP.NET MVC inside WebAssembly (browser-only, no server)',
    '- A Web Worker owns the DOM truth; main thread renders Worker-approved HTML',
    '- CephaSecurity has 9 layers (L1–L9):',
    '  L1: MutationObserver — detects unauthorized DOM changes',
    '  L2: SHA-256 integrity hash — verifies DOM state',
    '  L3: Shadow DOM — hides secure inputs from DevTools',
    '  L4: Worker-only authority — Worker is the single source of truth',
    '  L5: Anti-inspection — DevTools detection, clipboard guard',
    '  L6: Deferred repair queue — batches violation repairs',
    '  L7: MaterialDb verification — component integrity snapshots',
    '  L8: Genome object tracking — Merkle binding to system root',
    '  L9: Server exclusion signing — nonce-based allow-list for server pushes',
    '',
    '## Common Events You Explain:',
    '- stream_desync: DOM hash diverged from Worker stream (often caused by extension injection or server push without L9 nonce)',
    '- devtools_opened: Developer tools detected — secure fields are auto-protected',
    '- dom_mutation: Unauthorized DOM change detected by L1 MutationObserver',
    '- integrity_hash_mismatch: L2 SHA-256 hash check failed',
    '- secure_element_modified: L3 shadow DOM element was tampered',
    '- script_injection: Unauthorized script element detected',
    '- iframe_injection: Unauthorized iframe detected',
    '',
    '## Response Guidelines:',
    '- Be concise but thorough (2-4 sentences per explanation)',
    '- Start with the severity emoji: 🔴 FATAL, 🟠 ERROR, 🟡 WARN, 🔵 INFO, ⚪ DEBUG',
    '- Explain WHY the event happened and WHAT to do about it',
    '- When summarizing, group by severity and highlight patterns',
    '- Answer in the same language the user asks in (Arabic or English)',
    '- Use technical terms accurately — you understand WASM, Workers, DOM, SHA-256'
  ].join('\n');

  var _ollamaProxy = null;
  var _ollamaModel = null;
  var _scope = null;      // CephaStream isolated scope
  var _initialized = false;
  var _panelEl = null;
  var _chatBody = null;
  var _inputEl = null;
  var _sendBtn = null;
  var _statusEl = null;
  var _enforcementBanner = null;
  var _listeners = {};
  var _ollamaViaWorker = false; // true = use SharedWorker bridge

  function esc(s) {
    var d = document.createElement('div');
    d.textContent = s || '';
    return d.innerHTML;
  }

  function emit(event, data) {
    var cbs = _listeners[event] || [];
    for (var i = 0; i < cbs.length; i++) {
      try { cbs[i](data); } catch(e) { console.error('🤖 Sarmad event error:', e); }
    }
  }

  // ── Ollama Detection: SharedWorker → Cloud Shim → localhost proxy ──
  async function detectOllama() {
    // 1) Try SharedWorker bridge (OllamaHost in another tab)
    if (typeof CephaOllamaClient !== 'undefined') {
      try {
        CephaOllamaClient.connect();
        var workerResult = await new Promise(function(resolve) {
          var timeout = setTimeout(function() { resolve(false); }, 2000);
          CephaOllamaClient.on('connected', function(data) {
            clearTimeout(timeout);
            resolve(data.providerAvailable ? data : false);
          });
        });
        if (workerResult && workerResult.providerAvailable) {
          _ollamaModel = workerResult.model || 'OllamaHost';
          _ollamaViaWorker = true;
          _ollamaProxy = 'SharedWorker'; // flag for streamChat
          CephaOllamaClient.on('provider-lost', function() { _ollamaViaWorker = false; });
          CephaOllamaClient.on('provider-available', function(msg) {
            _ollamaViaWorker = true;
            _ollamaModel = msg.model || _ollamaModel;
          });
          return { ollama: 'connected', model: _ollamaModel, source: 'SharedWorker' };
        }
      } catch(_) {}
    }

    // 2) Try CephaOllamaCloud (cloud-backed Ollama shim — works everywhere)
    if (typeof CephaOllamaCloud !== 'undefined' && CephaOllamaCloud.isConfigured()) {
      try {
        var cloudHealth = await CephaOllamaCloud.health();
        if (cloudHealth && cloudHealth.ollama === 'connected') {
          _ollamaModel = cloudHealth.model || 'cloud';
          _ollamaViaWorker = false;
          _ollamaProxy = 'OllamaCloud'; // flag for streamChat
          console.log('🌐 CephaSarmad: Using CephaOllamaCloud (' + cloudHealth.provider + '/' + _ollamaModel + ')');
          return cloudHealth;
        }
      } catch(_) {}
    }

    // 3) Try localhost proxy
    var ports = [3005, 3006, 3007, 3008];
    for (var port of ports) {
      for (var proto of ['http', 'https']) {
        try {
          var url = proto + '://localhost:' + port + '/_ollama/health';
          var r = await fetch(url, { signal: AbortSignal.timeout(1500) });
          if (r.ok) {
            var data = await r.json();
            if (data.ollama === 'connected') {
              _ollamaProxy = proto + '://localhost:' + port;
              _ollamaModel = data.model || 'unknown';
              if (typeof CephaSecurity !== 'undefined' && CephaSecurity.registerTrustedSource) {
                CephaSecurity.registerTrustedSource(_ollamaProxy, 'Ollama/' + _ollamaModel);
              }
              return data;
            }
          }
        } catch(_) {}
      }
    }
    return null;
  }

  // ── Log Summary Builder ────────────────────────────────────
  function buildLogSummary() {
    if (typeof CephaSysLog === 'undefined') return 'No CephaSysLog available.';

    var secLogs = CephaSysLog.query({ category: 'SEC', limit: 50 });
    var repairLogs = CephaSysLog.query({ category: 'REPAIR', limit: 50 });
    var summary = CephaSysLog.getAnomalySummary();

    var parts = [];
    parts.push('Current session security state:');
    parts.push('- Total violations: ' + summary.totalViolations);
    parts.push('- Successful repairs: ' + summary.successfulRepairs);
    parts.push('- Worker heals: ' + summary.failedRepairs);

    if (typeof CephaSecurity !== 'undefined') {
      parts.push('- Enforcement: ' + (CephaSecurity.isEnforcementEnabled() ? 'ON (active protection)' : 'OFF (monitoring only)'));
    }

    var typeMap = summary.violationsByType || {};
    if (Object.keys(typeMap).length > 0) {
      parts.push('\nViolation breakdown:');
      for (var type in typeMap) {
        parts.push('  ' + type + ': ' + typeMap[type]);
      }
    }

    if (secLogs.length > 0) {
      parts.push('\nRecent security events (last ' + Math.min(secLogs.length, 10) + '):');
      var recent = secLogs.slice(-10);
      for (var i = 0; i < recent.length; i++) {
        var e = recent[i];
        var ts = e.timestamp ? e.timestamp.replace('T', ' ').substring(11, 19) : '';
        parts.push('  [' + ts + '] ' + (e.level || 'INFO') + ' — ' + (e.violationType || e.message));
      }
    }

    return parts.join('\n');
  }

  // ── Streaming Chat ─────────────────────────────────────────
  function addBubble(label, text, isSarmad) {
    if (!_chatBody) return null;
    var div = document.createElement('div');
    div.style.cssText = 'margin-bottom:10px;padding:10px 14px;border-radius:10px;line-height:1.6;font-size:0.88rem;' +
      'word-wrap:break-word;overflow-wrap:anywhere;white-space:pre-wrap;max-width:100%;min-width:0;' +
      (isSarmad
        ? 'background:rgba(76,175,80,0.08);border-left:3px solid #4caf50;'
        : 'background:rgba(33,150,243,0.08);border-left:3px solid #2196f3;');

    if (text) {
      div.innerHTML = '<strong>' + esc(label) + ':</strong> ' + formatSarmadText(text);
    } else {
      div.innerHTML = '<strong>' + esc(label) + ':</strong> <span class="sarmad-stream" style="display:inline-block;max-width:100%;overflow-wrap:anywhere;word-break:break-word;white-space:pre-wrap;vertical-align:top;"></span><span class="sarmad-cursor"></span>';
    }
    _chatBody.appendChild(div);
    _chatBody.scrollTop = _chatBody.scrollHeight;
    return div;
  }

  function formatSarmadText(text) {
    if (typeof CephaMd !== 'undefined') return CephaMd.render(text);
    return esc(text)
      .replace(/🔴|🟠|🟡|🔵|⚪/g, function(m) { return '<span style="font-size:1.1em">' + m + '</span>'; })
      .replace(/\n/g, '<br/>');
  }

  async function streamChat(message, contextPrefix) {
    if (!_ollamaProxy && !_ollamaViaWorker) return;

    // Create isolated scope on first use
    if (!_scope && typeof CephaStream !== 'undefined') {
      _scope = CephaStream.createScope({
        service: 'SarmadPanel',
        target: '#sarmad-panel',
        timeout: 120000
      });
    }

    // Abort any previous in-flight request
    if (_scope && _scope.streaming) _scope.abort();

    if (_sendBtn) { _sendBtn.disabled = true; _sendBtn.textContent = '⏳'; }

    var bubble = addBubble('🤖 سرمد', '', true);
    var textEl = bubble ? bubble.querySelector('.sarmad-stream') : null;
    var fullText = '';

    var fullMessage = (contextPrefix ? '[CONTEXT]\n' + contextPrefix + '\n[/CONTEXT]\n\n' : '') + message;

    if (_ollamaViaWorker && typeof CephaOllamaClient !== 'undefined') {
      // ── SharedWorker path (OllamaHost in another tab) ───────
      var hasFrame = typeof CephaSecurity !== 'undefined' && CephaSecurity.beginWorkerFrame;
      if (hasFrame) CephaSecurity.beginWorkerFrame();
      var _wordBuf = '';
      var _rafPending = false;
      function flushWordBuf() {
        _rafPending = false;
        if (textEl) textEl.innerHTML = formatSarmadText(fullText);
        if (_chatBody) _chatBody.scrollTop = _chatBody.scrollHeight;
      }
      await new Promise(function(resolve) {
        CephaOllamaClient.stream(fullMessage, {
          onChar: function(ch) {
            fullText += ch;
            if (!_rafPending) { _rafPending = true; requestAnimationFrame(flushWordBuf); }
          },
          onDone: function() {
            if (hasFrame) CephaSecurity.endWorkerFrame();
            resolve();
          },
          onError: function(err) {
            if (textEl) textEl.textContent = '⚠️ ' + (err.message || 'Connection error');
            if (hasFrame) CephaSecurity.endWorkerFrame();
            resolve();
          }
        });
      });
    } else if (_ollamaProxy === 'OllamaCloud' && typeof CephaOllamaCloud !== 'undefined') {
      // ── Cloud API path (CephaOllamaCloud — universal) ──────
      var _cloudRaf = false;
      function flushCloud() { _cloudRaf = false; if (textEl) textEl.innerHTML = formatSarmadText(fullText); if (_chatBody) _chatBody.scrollTop = _chatBody.scrollHeight; }
      await new Promise(function(resolve) {
        CephaOllamaCloud.stream(fullMessage, {
          onChar: function(ch) {
            fullText += ch;
            if (!_cloudRaf) { _cloudRaf = true; requestAnimationFrame(flushCloud); }
          },
          onDone: function() { resolve(); },
          onError: function(err) {
            if (textEl) textEl.textContent = '⚠️ ' + (err.message || 'Cloud API error');
            resolve();
          }
        });
      });
    } else {
      // ── Localhost proxy path ────────────────────────────────
      var streamUrl = _ollamaProxy + '/api/chat/stream?message=' + encodeURIComponent(fullMessage);

      if (_scope) {
        var _scopeRaf = false;
        _scope.tagElement(bubble);

        await _scope.stream(streamUrl, {
          onChar: function(ch) {
            fullText += ch;
            if (!_scopeRaf) { _scopeRaf = true; requestAnimationFrame(function() { _scopeRaf = false; if (textEl) textEl.innerHTML = formatSarmadText(fullText); if (_chatBody) _chatBody.scrollTop = _chatBody.scrollHeight; }); }
          },
          onError: function(err) {
            if (textEl) textEl.textContent = '⚠️ ' + (err.message || 'Connection error');
            console.error('🤖 Sarmad stream error:', err);
          },
          onAbort: function() {
            if (textEl && !fullText) textEl.textContent = '⏹️ ملغي';
          }
        });
      } else {
        // Fallback: direct fetch — L9 signed (no CephaStream)
        var _fallbackRaf = false;
        var _sarmadFallbackLink = null;
        try {
          if (typeof CephaSecurity !== 'undefined' && CephaSecurity.createSourceLink) {
            _sarmadFallbackLink = CephaSecurity.createSourceLink(streamUrl, '#sarmad-panel', 'SarmadFallback');
            _sarmadFallbackLink.tagElement(bubble);
            _sarmadFallbackLink.begin();
          }
          var resp = await fetch(streamUrl);
          if (!resp.ok) throw new Error('Proxy: ' + resp.status);
          var reader = resp.body.getReader();
          var decoder = new TextDecoder();
          var buf = '';
          while (true) {
            var result = await reader.read();
            if (result.done) break;
            buf += decoder.decode(result.value, { stream: true });
            var parts = buf.split('\n');
            buf = parts.pop() || '';
            for (var i = 0; i < parts.length; i++) {
              var line = parts[i].trim();
              if (!line) continue;
              try {
                var d = JSON.parse(line);
                if (d.type === 'char' && textEl) {
                  fullText += d.value;
                  if (!_fallbackRaf) { _fallbackRaf = true; requestAnimationFrame(function() { _fallbackRaf = false; textEl.innerHTML = formatSarmadText(fullText); if (_chatBody) _chatBody.scrollTop = _chatBody.scrollHeight; }); }
                }
              } catch(_) {}
            }
          }
        } catch(err) {
          if (textEl) textEl.textContent = '⚠️ ' + (err.message || 'Connection error');
          console.error('🤖 Sarmad stream error:', err);
        } finally {
          if (_sarmadFallbackLink) _sarmadFallbackLink.end();
        }
      }
    }

    var cur = bubble ? bubble.querySelector('.sarmad-cursor') : null;
    if (cur) cur.remove();

    if (_sendBtn) { _sendBtn.disabled = false; _sendBtn.textContent = '↵'; }
    if (_inputEl) _inputEl.focus();
    emit('response', { message: message, response: fullText });

    if (typeof CephaSysLog !== 'undefined') {
      CephaSysLog.log('INFO', 'SARMAD', 'Query: ' + message.substring(0, 80), { responseLength: fullText.length });
    }
  }

  // ── UI Construction ────────────────────────────────────────
  function buildPanel() {
    if (_panelEl) return;

    _panelEl = document.createElement('div');
    _panelEl.id = 'sarmad-panel';
    _panelEl.setAttribute('data-cepha-secure-skip', '');
    _panelEl.style.cssText = [
      'position:fixed;bottom:0;left:0;right:0;z-index:9999;',
      'background:#1a1a2e;color:#e0e0e0;',
      'border-top:2px solid #4caf50;',
      'font-family:"Segoe UI",sans-serif;',
      'transform:translateY(100%);transition:transform .6s cubic-bezier(.4,0,.2,1);',
      'max-height:45vh;display:flex;flex-direction:column;',
      'box-shadow:0 -4px 20px rgba(0,0,0,.4);'
    ].join('');

    // Header bar
    var header = document.createElement('div');
    header.style.cssText = 'padding:10px 16px;display:flex;align-items:center;justify-content:space-between;background:#0d0d1a;border-bottom:1px solid #333;flex-shrink:0;';

    var titleGroup = document.createElement('div');
    titleGroup.style.cssText = 'display:flex;align-items:center;gap:10px;';
    titleGroup.innerHTML = '<span style="font-size:1.2rem">🤖</span>' +
      '<strong style="color:#4caf50;font-size:0.95rem;">سرمد يراقب</strong>' +
      '<span style="font-size:0.75rem;color:#888;font-family:monospace">Sarmad Watches v' + SARMAD_VERSION + '</span>';

    _statusEl = document.createElement('span');
    _statusEl.style.cssText = 'font-size:0.75rem;color:#888;';
    _statusEl.textContent = 'Connecting...';

    var closeBtn = document.createElement('button');
    closeBtn.textContent = '✕';
    closeBtn.style.cssText = 'background:none;border:none;color:#888;font-size:1.1rem;cursor:pointer;padding:4px 8px;border-radius:4px;';
    closeBtn.addEventListener('click', function() { CephaSarmad.hide(); });
    closeBtn.addEventListener('mouseenter', function() { this.style.color = '#fff'; this.style.background = '#333'; });
    closeBtn.addEventListener('mouseleave', function() { this.style.color = '#888'; this.style.background = 'none'; });

    header.appendChild(titleGroup);
    var rightGroup = document.createElement('div');
    rightGroup.style.cssText = 'display:flex;align-items:center;gap:12px;';
    rightGroup.appendChild(_statusEl);
    rightGroup.appendChild(closeBtn);
    header.appendChild(rightGroup);

    // Enforcement banner
    _enforcementBanner = document.createElement('div');
    _enforcementBanner.style.cssText = 'padding:6px 16px;font-size:0.8rem;text-align:center;background:rgba(255,152,0,0.15);color:#ffb74d;border-bottom:1px solid #333;flex-shrink:0;transition:opacity .5s ease,transform .5s ease;';
    _enforcementBanner.textContent = '🛡️ Enforcement OFF — violations logged only';

    // Chat body
    _chatBody = document.createElement('div');
    _chatBody.style.cssText = 'flex:1;overflow-y:auto;overflow-x:hidden;padding:12px 16px;min-height:80px;scroll-behavior:smooth;min-width:0;';

    // Input row
    var inputRow = document.createElement('div');
    inputRow.id = 'sarmad-input-row';
    inputRow.style.cssText = 'padding:10px 16px;display:flex;gap:8px;border-top:1px solid #333;background:#0d0d1a;flex-shrink:0;opacity:0;transform:translateY(10px);transition:opacity .4s ease .2s,transform .4s ease .2s;';

    _inputEl = document.createElement('input');
    _inputEl.type = 'text';
    _inputEl.placeholder = 'اسأل سرمد عن أي سجل أمني...';
    _inputEl.setAttribute('dir', 'auto');
    _inputEl.style.cssText = 'flex:1;padding:8px 12px;border-radius:6px;border:1px solid #444;background:#2a2a3e;color:#e0e0e0;font-size:0.88rem;outline:none;transition:border-color .2s;';
    _inputEl.addEventListener('focus', function() { this.style.borderColor = '#4caf50'; });
    _inputEl.addEventListener('blur', function() { this.style.borderColor = '#444'; });
    _inputEl.addEventListener('keydown', function(e) {
      if (e.key === 'Enter') doUserQuery();
    });

    _sendBtn = document.createElement('button');
    _sendBtn.textContent = '↵';
    _sendBtn.style.cssText = 'padding:8px 16px;border-radius:6px;border:none;background:#4caf50;color:#fff;font-size:1rem;cursor:pointer;font-weight:700;transition:background .2s;min-width:44px;';
    _sendBtn.addEventListener('click', doUserQuery);
    _sendBtn.addEventListener('mouseenter', function() { this.style.background = '#388e3c'; });
    _sendBtn.addEventListener('mouseleave', function() { this.style.background = '#4caf50'; });

    inputRow.appendChild(_inputEl);
    inputRow.appendChild(_sendBtn);

    _panelEl.appendChild(header);
    _panelEl.appendChild(_enforcementBanner);
    _panelEl.appendChild(_chatBody);
    _panelEl.appendChild(inputRow);
    document.body.appendChild(_panelEl);
  }

  function doUserQuery() {
    if (!_inputEl) return;
    var msg = _inputEl.value.trim();
    if (!msg) return;
    _inputEl.value = '';

    addBubble('👤 أنت', msg, false);

    var context = buildLogSummary();
    streamChat(msg, context);
  }

  // ── Show/Hide with Animation ───────────────────────────────
  function showPanel() {
    buildPanel();
    requestAnimationFrame(function() {
      _panelEl.style.transform = 'translateY(0)';
    });

    // Animate enforcement banner
    if (_enforcementBanner) {
      _enforcementBanner.style.opacity = '0';
      _enforcementBanner.style.transform = 'translateY(8px)';
      setTimeout(function() {
        _enforcementBanner.style.opacity = '1';
        _enforcementBanner.style.transform = 'translateY(0)';
      }, 400);
    }

    // Show input row with delay
    setTimeout(function() {
      var inputRow = document.getElementById('sarmad-input-row');
      if (inputRow) {
        inputRow.style.opacity = '1';
        inputRow.style.transform = 'translateY(0)';
      }
    }, 600);

    emit('show', {});
  }

  function hidePanel() {
    if (_panelEl) {
      _panelEl.style.transform = 'translateY(100%)';
    }
    emit('hide', {});
  }

  function updateEnforcementBanner() {
    if (!_enforcementBanner) return;
    if (typeof CephaSecurity === 'undefined') return;
    var enf = CephaSecurity.isEnforcementEnabled();
    _enforcementBanner.textContent = enf
      ? '🛡️ Enforcement ON — violations will be repaired automatically'
      : '🛡️ Enforcement OFF — violations logged only';
    _enforcementBanner.style.background = enf
      ? 'rgba(76,175,80,0.15)'
      : 'rgba(255,152,0,0.15)';
    _enforcementBanner.style.color = enf ? '#81c784' : '#ffb74d';
  }

  // ── Initial Summary ────────────────────────────────────────
  async function generateInitialSummary() {
    if (!_ollamaProxy) {
      addBubble('🤖 سرمد', '⚠️ لم يتم العثور على Ollama — التحليل غير متاح. شغّل Ollama Proxy من لوحة التحكم.', true);
      return;
    }

    var logState = buildLogSummary();
    var prompt = 'You just activated as the security guardian. Here is the current session state:\n\n' +
      logState +
      '\n\nProvide a brief initial security assessment (3-5 sentences). ' +
      'Highlight any concerning patterns. If everything is clean, confirm that the system is healthy. ' +
      'Respond in Arabic (العربية) primarily, with English technical terms where appropriate.';

    await streamChat(prompt, null);
  }

  // ── Initialization ─────────────────────────────────────────
  async function init() {
    if (_initialized) return;
    _initialized = true;

    console.log('🤖 CephaSarmad: Initializing v' + SARMAD_VERSION);

    var data = await detectOllama();

    buildPanel();

    if (data && data.ollama === 'connected') {
      if (_statusEl) {
        _statusEl.innerHTML = '🟢 <span style="color:#4caf50">' + esc(_ollamaModel) + '</span>';
      }
      console.log('🤖 CephaSarmad: Connected to Ollama (' + _ollamaModel + ')');
    } else {
      if (_statusEl) {
        _statusEl.innerHTML = '🔴 <span style="color:#f44336">Offline</span>';
      }
      console.log('🤖 CephaSarmad: Ollama not available — limited mode');
    }

    // Listen for enforcement changes
    if (typeof CephaSysLog !== 'undefined') {
      CephaSysLog.on('SEC', function(entry) {
        if (!_panelEl || _panelEl.style.transform !== 'translateY(0)' && _panelEl.style.transform !== 'translateY(0px)') return;
        updateEnforcementBanner();
      });
    }

    emit('init', { proxy: _ollamaProxy, model: _ollamaModel });
  }

  // ── Public API ─────────────────────────────────────────────
  window.CephaSarmad = {
    init: init,

    show: function() {
      init().then(function() {
        showPanel();
        updateEnforcementBanner();
        // Auto-generate initial summary if chat body is empty
        if (_chatBody && _chatBody.children.length === 0) {
          setTimeout(generateInitialSummary, 800);
        }
      });
    },

    hide: hidePanel,

    toggle: function() {
      if (_panelEl && (_panelEl.style.transform === 'translateY(0)' || _panelEl.style.transform === 'translateY(0px)')) {
        hidePanel();
      } else {
        CephaSarmad.show();
      }
    },

    isVisible: function() {
      return _panelEl && (_panelEl.style.transform === 'translateY(0)' || _panelEl.style.transform === 'translateY(0px)');
    },

    ask: function(question) {
      if (!_panelEl) CephaSarmad.show();
      var context = buildLogSummary();
      addBubble('👤 أنت', question, false);
      return streamChat(question, context);
    },

    explainEvent: function(eventType, details) {
      var question = 'Explain this security event: ' + eventType;
      if (details) question += '\nDetails: ' + JSON.stringify(details);
      return CephaSarmad.ask(question);
    },

    on: function(event, cb) {
      if (!_listeners[event]) _listeners[event] = [];
      _listeners[event].push(cb);
    },

    off: function(event, cb) {
      if (!_listeners[event]) return;
      _listeners[event] = _listeners[event].filter(function(c) { return c !== cb; });
    },

    getStatus: function() {
      return {
        version: SARMAD_VERSION,
        initialized: _initialized,
        ollamaProxy: _ollamaProxy,
        ollamaModel: _ollamaModel,
        isStreaming: _scope ? _scope.streaming : false,
        visible: CephaSarmad.isVisible()
      };
    }
  };

  console.log('🤖 CephaSarmad: Module loaded (v' + SARMAD_VERSION + ')');

  // Auto-init: detect Ollama and show panel when app is ready
  function autoInit() {
    detectOllama().then(function(result) {
      if (result) {
        console.log('🤖 CephaSarmad: Ollama detected at ' + _ollamaProxy + ' — auto-showing panel');
        CephaSarmad.show();
      } else {
        console.log('🤖 CephaSarmad: No Ollama proxy found — panel available via CephaSarmad.show()');
      }
    });
  }

  if (document.readyState === 'complete') {
    setTimeout(autoInit, 2000);
  } else {
    window.addEventListener('load', function() { setTimeout(autoInit, 2000); });
  }
})();
