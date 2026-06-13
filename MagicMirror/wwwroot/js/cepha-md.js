// 🧬 CephaMd — Lightweight Markdown Renderer for Cepha Material
// A Cepha Material plugin for rendering LLM responses with proper
// markdown formatting, RTL support, and inline action buttons.
//
// Inspired by noteBay rendering approach — minimal, secure, no deps.
// Designed for streaming: call render() on each accumulated chunk.
//
// Features:
//   - **bold**, *italic*, `inline code`, ```code blocks```
//   - ## headings, > blockquotes, - lists
//   - Severity emojis (🔴🟠🟡🔵⚪) enlarged
//   - [action:toggle-enforcement] → clickable UI protection button
//   - XSS-safe: all text is escaped before decoration

const CephaMd = (() => {
  'use strict';

  // HTML-escape to prevent XSS
  function esc(s) {
    const d = document.createElement('div');
    d.textContent = s || '';
    return d.innerHTML;
  }

  // Render markdown text to safe HTML
  function render(text, options) {
    if (!text) return '';
    const opt = options || {};

    // Phase 1: Escape HTML entities
    let html = esc(text);

    // Phase 2: Code blocks (``` ... ```) — must be before inline formatting
    html = html.replace(/```(\w*)\n([\s\S]*?)```/g, function(_, lang, code) {
      return '<div class="cmd-codeblock" data-lang="' + (lang || '') + '">' +
        '<pre style="margin:0;padding:10px 14px;border-radius:8px;' +
        'background:var(--cepha-surface,#1a1f2e);color:var(--cepha-text,#e8edf5);' +
        'font-family:var(--cepha-font-mono,monospace);font-size:0.82rem;' +
        'overflow-x:auto;direction:ltr;text-align:left;white-space:pre-wrap;word-break:break-word">' +
        code + '</pre></div>';
    });

    // Phase 3: Inline code (`code`)
    html = html.replace(/`([^`]+)`/g,
      '<code style="background:var(--cepha-primary-alpha,rgba(102,126,234,0.12));' +
      'padding:1px 5px;border-radius:4px;font-family:var(--cepha-font-mono,monospace);' +
      'font-size:0.85em;direction:ltr">$1</code>');

    // Phase 4: Bold (**text**)
    html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

    // Phase 5: Italic (*text*) — avoid matching ** already processed
    html = html.replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, '<em>$1</em>');

    // Phase 6: Headers (## at start of line)
    html = html.replace(/^### (.+)$/gm,
      '<div style="font-size:1rem;font-weight:700;margin:8px 0 4px;color:var(--cepha-text)">$1</div>');
    html = html.replace(/^## (.+)$/gm,
      '<div style="font-size:1.1rem;font-weight:700;margin:10px 0 4px;color:var(--cepha-text)">$1</div>');
    html = html.replace(/^# (.+)$/gm,
      '<div style="font-size:1.2rem;font-weight:700;margin:12px 0 6px;color:var(--cepha-text)">$1</div>');

    // Phase 7: Blockquotes (> at start of line)
    html = html.replace(/^&gt; (.+)$/gm,
      '<div style="border-left:3px solid var(--cepha-primary,#667eea);padding:4px 12px;' +
      'margin:4px 0;color:var(--cepha-text-secondary,#718096);font-style:italic">$1</div>');

    // Phase 8: Unordered lists (- or * at start of line)
    html = html.replace(/^[-*] (.+)$/gm,
      '<div style="padding-left:16px;position:relative;margin:2px 0">' +
      '<span style="position:absolute;left:4px;color:var(--cepha-primary,#667eea)">•</span>$1</div>');

    // Phase 9: Numbered lists (1. 2. etc.)
    html = html.replace(/^(\d+)\. (.+)$/gm,
      '<div style="padding-left:20px;position:relative;margin:2px 0">' +
      '<span style="position:absolute;left:0;color:var(--cepha-primary,#667eea);font-weight:600">$1.</span>$2</div>');

    // Phase 10: Severity emojis — enlarge
    html = html.replace(/🔴|🟠|🟡|🔵|⚪|🟢/g, function(m) {
      return '<span style="font-size:1.15em">' + m + '</span>';
    });

    // Phase 11: Action tokens — clickable buttons
    // [action:toggle-enforcement] → 🛡️ toggle button
    html = html.replace(/\[action:toggle-enforcement\]/gi, _renderEnforcementButton());

    // Also detect natural language recommendations to enable enforcement
    if (!opt.noAutoButton) {
      const enforcementPatterns = [
        /يُنصح.{0,20}(تفعيل|الحماية|enforcement)/i,
        /recommend.{0,20}(enabl|enforcement|protection)/i,
        /activate.{0,15}enforcement/i,
        /تفعيل.{0,15}(الإصلاح|الحماية|حماية)/i
      ];
      let hasRecommendation = false;
      for (let i = 0; i < enforcementPatterns.length; i++) {
        if (enforcementPatterns[i].test(text)) { hasRecommendation = true; break; }
      }
      if (hasRecommendation) {
        html += _renderEnforcementButton();
      }
    }

    // Phase 12: Line breaks (preserve newlines as <br/>)
    // But skip inside code blocks
    html = html.replace(/\n/g, '<br/>');

    return html;
  }

  function _renderEnforcementButton() {
    const isOn = typeof CephaSecurity !== 'undefined' && CephaSecurity.isEnforcementEnabled();
    const label = isOn ? '🛡️ الحماية مفعّلة ✓' : '🛡️ تفعيل حماية الواجهة';
    const bg = isOn ? 'var(--cepha-success,#48bb78)' : 'var(--cepha-primary,#667eea)';
    return '<button class="cmd-action-btn" data-action="toggle-enforcement" ' +
      'style="display:inline-block;margin:8px 0 4px;padding:6px 16px;' +
      'border:none;border-radius:var(--cepha-radius,8px);' +
      'background:' + bg + ';color:#fff;font-size:0.82rem;font-weight:600;' +
      'font-family:inherit;cursor:pointer;transition:opacity 0.2s,transform 0.15s;' +
      'box-shadow:var(--cepha-shadow-sm,0 1px 2px rgba(0,0,0,0.1))"' +
      ' onclick="CephaMd._handleAction(this,\'toggle-enforcement\')">' +
      label + '</button>';
  }

  // Action handler — called from inline onclick
  function _handleAction(btn, action) {
    if (action === 'toggle-enforcement') {
      if (typeof CephaSecurity !== 'undefined') {
        const result = CephaSecurity.toggleEnforcement();
        btn.textContent = result ? '🛡️ الحماية مفعّلة ✓' : '🛡️ تفعيل حماية الواجهة';
        btn.style.background = result
          ? 'var(--cepha-success,#48bb78)'
          : 'var(--cepha-primary,#667eea)';
        // Pulse animation
        btn.style.transform = 'scale(0.95)';
        setTimeout(function() { btn.style.transform = ''; }, 150);
      }
    }
  }

  return { render, esc, _handleAction };
})();
