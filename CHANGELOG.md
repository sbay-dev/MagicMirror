# Changelog

## v1.0.5 - 2026-06-18

### Fixed
- Translation status now shows whether the result came from the dedicated
  Sarmad AI gateway, MT fallback, mixed sources, or original fallback.
- Dictionary output now converts model Markdown/tables into structured UI cards
  instead of showing raw overlapping text.
- Dictionary cards follow the target-language direction, keeping Arabic
  right-to-left while isolating Latin technical tokens.
- Dictionary cards now use per-paragraph first-strong direction with Unicode
  isolates, larger readable text, calibrated click hit-testing, and a visible
  sentence-selection action before dictionary review.
- Word selection now keeps the previous full-line rendering, but restored strict
  v1.0.1-style hit geometry: a word is selected only when the registered word
  region is actually hit.
- Selection fallback/border highlighting no longer draws repeated or double
  boxes for every matching word occurrence.
- Source/OCR text clicks use the capture-derived OCR block geometry from the
  earlier precise selection workflow instead of approximate source word regions.
- Selecting a translated word now also marks its source OCR block and uses
  preserved OCR line geometry when available; otherwise the line marker is an
  inferred visual aid, not a source-word guarantee.
- The trusted reading path now uses native MAUI/WinUI text controls instead of
  canvas text blocks, improving bidi behavior, selection, copy, and context-menu
  reliability.
- Dictionary technical provenance is separated from the linguistic answer into a
  dedicated proof tab, with OCR/source rectangles, local Merkle hashes, root, and
  path kept out of the main glossary result.
- Translation fallback is Sarmad-first and no longer silently mixes MT into a
  Sarmad result. MT is off by default and, when enabled, requires explicit
  per-run confirmation before translating the whole capture through Google
  `gtx` / MyMemory.

### Added
- OCR translation captures now default to a 2x high-quality screen image, giving
  Tesseract/Windows OCR larger glyphs while keeping the live preview lightweight.
- Tesseract tessdata no longer defaults to a developer workstation path; it is
  auto-discovered from the app, Tesseract installation, or `TESSDATA_PREFIX`.
- The detached reader window provides independent text size, wrapping, copy,
  ink/background/opacity controls, and dictionary review for selected text.
- The transparent mirror editor and detached reader right-click menus expose
  **معجم المحدد**, **نسخ**, and **تحديد الكل**.
- MT fallback is documented and labelled as non-academic quality when used;
  users must opt in before it can be offered.
- The mirror window can now be resized from the glass surface with mouse wheel
  or touch pinch; in translated mode, plain wheel scrolls overflow while
  `Ctrl` + wheel resizes the window.
- Wheel/pinch resize now keeps the mirror visible throughout the resize burst
  instead of using the transparent drag path.
- Releasing drag/resize/zoom now refreshes the mirror with a full-resolution
  settled capture, while ordinary idle preview remains lightweight.
- Dictionary alternatives can now be adopted into the visible translation and
  saved as local glossary-memory rules. Future translation/dictionary prompts
  receive matching user-approved rules as high-priority guidance.
- Translation now reveals returned text one Unicode text element at a time in
  the overlay, so the visible delivery is character-by-character instead of
  replacing whole batches at once.

## v1.0.4 - 2026-06-13

### Added
- Dedicated Cloudflare Worker gateway under
  `cloudflare/magicmirror-sarmad-gateway`, with Wrangler config, deployment
  script, and manual GitHub Actions workflow template.
- Published the dedicated gateway at
  `https://magicmirror-sarmad-gateway.2sa.workers.dev` and made it the native
  app default `GatewayBaseUrl`.

## v1.0.3 - 2026-06-13

### Fixed
- Root-caused the Sarmad failure: the canonical documentation gateway was still
  deployed against deprecated `@cf/openai/gpt-oss-120b`, and ignored client
  model overrides.
- Updated the Magic Mirror web layer and native defaults to
  `@cf/openai/gpt-oss-20b`.
- Disabled the broken `wmr-doc.pages.dev` gateway as a default product fallback;
  blank gateway settings report no dictionary gateway, while translation uses MT
  only when the user explicitly enables the non-academic fallback.

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
