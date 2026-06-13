# Changelog

## Unreleased

### Added
- Dedicated Cloudflare Worker gateway under
  `cloudflare/magicmirror-sarmad-gateway`, with Wrangler config, deployment
  script, and manual GitHub Actions workflow template.

## v1.0.3 - 2026-06-13

### Fixed
- Root-caused the Sarmad failure: the canonical documentation gateway was still
  deployed against deprecated `@cf/openai/gpt-oss-120b`, and ignored client
  model overrides.
- Updated the Magic Mirror web layer and native defaults to
  `@cf/openai/gpt-oss-20b`.
- Disabled the broken `wmr-doc.pages.dev` gateway as a default product fallback;
  blank gateway settings now mean MT fallback for translation and an explicit
  "dictionary gateway not configured" status for dictionary analysis.

## v1.0.2 - 2026-06-13

### Fixed
- Dictionary results now render as structured cards with section headings instead
  of a single raw label, preventing Arabic/English bidi overlap and unreadable
  line wrapping.
- Gateway failures now appear as a clear dictionary status card instead of a
  malformed analysis block, so the app remains usable when gpt-oss-120b is
  temporarily unavailable.

## v1.0.1 - 2026-06-13

### Fixed
- Dictionary panel can now be repositioned reliably with on-card arrow controls
  and a larger drag handle.
- Arabic click selection now records the exact selected rendered/source hit
  rectangle while outlining the corresponding source block without covering the
  document with a large opaque mask.
- Dictionary requests now send only the selected term plus compact nearby
  context, retry progressively smaller payloads, and surface gateway failures
  from the top of the panel.

## v1.0.0 - 2026-06-13

Initial public release of Magic Mirror.

### Added
- Transparent Windows MAUI overlay that is excluded from self-capture.
- Live see-through preview, drag/resize, body-only transparency while moving,
  and double-click live/translation toggle.
- Capture -> OCR/UI Automation -> font detection -> translation -> overlay
  pipeline.
- Arabic-first document rendering with RTL/LTR mixed-run handling for terms
  such as `CNS`, `LCNS`, and `QKV`.
- Reader-page mode, configurable translation background color/opacity, and
  translated ink color from both settings and the overlay.
- Copy original/translated text, mouse-wheel scrolling, and text-size/layout
  controls.
- Contextual dictionary workflow with click-to-select text, rendered hit
  testing, draggable dictionary panel, structured output, and strict
  gpt-oss-120b prompting.
- Formal author specifications and executable directives under
  `docs/specifications/`.

### Fixed
- Translate-button crash guards.
- OCR row/paragraph merge order for RTL output.
- Mixed English terms starting false Arabic lines.
- Oversized background masks that covered surrounding document content.
- New overlay style controls clipping and black-drop flash during drag.
- Dictionary selection conflict with drag/drop; selection now works by normal
  click on visible text.
