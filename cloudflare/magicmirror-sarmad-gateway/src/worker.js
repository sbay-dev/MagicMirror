const GATEWAY_NAME = 'magicmirror-sarmad-gateway';
const DEFAULT_MODEL = '@cf/openai/gpt-oss-20b';
const DEPRECATED_MODELS = new Set(['@cf/openai/gpt-oss-120b']);
const MAX_PROMPT_CHARS = 32000;
const MAX_CONTEXT_CHARS = 12000;

const JSON_HEADERS = {
  'content-type': 'application/json; charset=utf-8',
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'GET,POST,OPTIONS',
  'access-control-allow-headers': 'content-type,authorization',
  'access-control-max-age': '86400'
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: JSON_HEADERS });
    }

    if (request.method === 'GET' && (url.pathname === '/' || url.pathname === '/api/sarmad/health')) {
      return json({
        ok: true,
        gateway: GATEWAY_NAME,
        endpoint: '/api/sarmad/ask',
        model: DEFAULT_MODEL
      });
    }

    if (request.method !== 'POST' || url.pathname !== '/api/sarmad/ask') {
      return json({ error: 'not_found', message: 'Use POST /api/sarmad/ask.' }, 404);
    }

    if (!env.AI || typeof env.AI.run !== 'function') {
      return json({ error: 'ai_binding_missing', message: 'Cloudflare Workers AI binding AI is not configured.' }, 503);
    }

    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: 'invalid_json', message: 'Request body must be JSON.' }, 400);
    }

    const prompt = limitText(body.prompt, MAX_PROMPT_CHARS);
    if (!prompt) {
      return json({ error: 'missing_prompt', message: 'Field prompt is required.' }, 400);
    }

    const mode = limitText(body.mode, 64) || 'docs-assistant';
    const surface = limitText(body.surface, 64) || 'magic-mirror';
    const language = limitText(body.language, 24) || 'ar';
    const context = limitText(body.context, MAX_CONTEXT_CHARS);
    const requestedModel = limitText(body.model, 160);
    const model = normalizeModel(requestedModel);

    const system = buildSystemPrompt(mode, surface, language);
    const user = context
      ? `Context:\n${context}\n\nTask:\n${prompt}`
      : prompt;

    try {
      const result = await env.AI.run(model, {
        messages: [
          { role: 'system', content: system },
          { role: 'user', content: user }
        ],
        temperature: mode.includes('dictionary') || mode.includes('lexicon') ? 0.2 : 0.15,
        max_tokens: mode.includes('dictionary') || mode.includes('lexicon') ? 1800 : 4096
      });

      const answer = extractAnswer(result);
      if (!answer) {
        return json({
          error: 'empty_model_response',
          message: 'Workers AI returned no textual answer.',
          model
        }, 502);
      }

      return json({
        answer,
        model,
        gateway: GATEWAY_NAME
      });
    } catch (error) {
      return json({
        error: 'inference_failed',
        message: publicErrorMessage(error),
        model
      }, 502);
    }
  }
};

function buildSystemPrompt(mode, surface, language) {
  const base = [
    'You are Sarmad, the dedicated AI gateway for Magic Mirror.',
    `Surface: ${surface}. Target language: ${language}.`,
    'Return only the requested answer. Do not include hidden reasoning.',
    'Preserve technical identifiers and acronyms unless the user explicitly asks to expand them.',
    'For Arabic output, use formal document-grade Arabic and keep Latin technical tokens readable.'
  ];

  if (mode.includes('dictionary') || mode.includes('lexicon')) {
    base.push(
      'Dictionary mode: provide a lexical entry, domain classification, at least five alternatives, fit/non-fit notes, and a decisive final recommendation.',
      'Use the nearby context to disambiguate; if context is insufficient, say that explicitly without inventing certainty.'
    );
  } else {
    base.push(
      'Translation mode: preserve line order, numbering, negation, units, citations, and document register.',
      'If the prompt asks for numbered lines, output exactly the same count and numbering.'
    );
  }

  return base.join('\n');
}

function normalizeModel(model) {
  const value = (model || '').trim();
  if (!value || DEPRECATED_MODELS.has(value)) {
    return DEFAULT_MODEL;
  }

  return value.startsWith('@cf/') ? value : DEFAULT_MODEL;
}

function extractAnswer(result) {
  if (typeof result === 'string') {
    return result.trim();
  }

  if (!result || typeof result !== 'object') {
    return '';
  }

  const direct = firstText(
    result.answer,
    result.response,
    result.output_text,
    result.content,
    result.result
  );
  if (direct) {
    return direct;
  }

  const choice = Array.isArray(result.choices) ? result.choices[0] : null;
  return firstText(choice?.message?.content, choice?.text);
}

function firstText(...values) {
  for (const value of values) {
    const text = textFromValue(value);
    if (text) {
      return text;
    }
  }

  return '';
}

function textFromValue(value) {
  if (typeof value === 'string') {
    return value.trim();
  }

  if (Array.isArray(value)) {
    return value.map(textFromValue).filter(Boolean).join('').trim();
  }

  if (value && typeof value === 'object') {
    return firstText(value.content, value.text, value.response, value.answer);
  }

  return '';
}

function limitText(value, maxChars) {
  const text = typeof value === 'string' ? value.trim() : '';
  if (text.length <= maxChars) {
    return text;
  }

  return `${text.slice(0, maxChars - 1).trimEnd()}…`;
}

function publicErrorMessage(error) {
  if (!error) {
    return 'Unknown Workers AI error.';
  }

  const message = typeof error.message === 'string' ? error.message : String(error);
  return message.replace(/Bearer\s+[A-Za-z0-9._-]+/gi, 'Bearer [redacted]');
}

function json(payload, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: JSON_HEADERS
  });
}
