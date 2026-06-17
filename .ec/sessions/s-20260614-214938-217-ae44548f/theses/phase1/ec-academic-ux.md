# Phase 1 Thesis - Academic UX

Reviewer: ec-academic-ux
Model: claude-opus-4.7
Mode: independent read-only review

## Scope

Semantic display quality, source/translation linking, dictionary presentation, OCR/capture posture, and conformance to scientific or publisher formatting expectations for Magic Mirror v1.0.5.

## Method

The reviewer traced overlay rendering, selection/linking, OCR/capture, translation/dictionary/glossary, release docs, security docs, and author directives. It assessed representative academic-reader flows including mixed RTL/LTR selection, two-column papers, citations/equations, and dictionary adoption.

## Findings

- The product is a polished translated reading overlay with document-role typography, source/translation status disclosure, glossary memory, dictionary cards, cancellation, and domain-aware Sarmad prompts.
- Publisher-grade display remains unsupported: no real text shaping engine, no kashida/justification/hyphenation, no table/equation/footnote/citation structural model, no export, and no accessibility surface.
- Earlier blockers included MT fallback privacy, weak MT trigger logic, developer-only tessdata, deprecated bidi embeddings, and column fusion. Several of those were addressed after Phase 1: MT fallback became opt-in/off by default, tessdata auto-discovery replaced the hardcoded path, bidi isolates moved to U+2066/U+2067/U+2069, and row splitting was hardened for large column gaps.
- Source linking is still not publisher-exact. Source-word linkage is absent, and translated-to-source mapping must be described as block or measured OCR-line provenance when actual source lines exist.

## Blockers

- No publisher-grade document model for tables, equations, citations, footnotes, columns, or structured export.
- No Arabic publishing composition engine or bundled scholarly Arabic font stack.
- No source-word exactness.
- No accessibility/compliance documentation.
- No automated visual/interaction regression evidence for selection and layout.

## Evidence

- `MagicMirror.Native\Mirror\MirrorDrawable.cs`
- `MagicMirror.Native\Mirror\MirrorEngine.cs`
- `MagicMirror.Native\Pages\MirrorOverlayPage.xaml(.cs)`
- `MagicMirror.Native\Mirror\SarmadTranslationService.cs`
- `MagicMirror.Native\Mirror\GlossaryMemoryStore.cs`
- `docs\USER_GUIDE.md`, `SECURITY.md`, `docs\releases\v1.0.5.md`

## Verdict

FAIL for "professional academic publishing" release. PASS WITH CONDITIONS only for a reader-assistant preview after privacy/tessdata/source-claim fixes and user visual approval.

## Vote-ready assertions

- Reject publisher-grade/adoption claims.
- Permit a limited academic reading-aid preview with explicit limitations.
- Keep MT fallback opt-in and documented.
- Limit source-link claims to block/measured OCR-line provenance.
- Keep professional release blocked until testing, evaluation, export, accessibility, and validation exist.
