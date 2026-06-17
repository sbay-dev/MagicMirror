# Phase 1 Thesis - Linking Precision

Reviewer: ec-linking-precision
Model: gpt-5.3-codex
Mode: independent static trace audit

## Scope

Selection-link precision and scientific honesty in the OCR/UIA region pipeline, translated hit geometry, pointer hit resolution, and translated-to-source counterpart mapping.

## Method

The reviewer traced OCR/UIA region creation through block construction, rendered hit-region geometry, pointer selection, counterpart mapping, and release/docs claims about precision.

## Findings

- Coherent block-level source/translation linkage exists through `TranslatedBlock` and shared geometry.
- Line and sentence linking is not exact when document OCR rows are merged into paragraphs unless true OCR source-line provenance is preserved and used.
- Word hit rectangles are rendered heuristics based on token widths rather than full glyph-run layout.
- Nearest-hit behavior and line-ratio mapping can drift in dense mixed RTL/LTR academic text.
- Documentation and release language must not overclaim exact source-word or exact publisher-grade mapping.

## Blockers

- Exact source-line linkage requires preserved OCR line geometry IDs/provenance and deterministic tests.
- Robust mixed RTL/LTR word selection requires measured text layout/glyph metrics or constrained tolerances.
- Public claims must be downgraded to block/measured-line visual guidance unless implementation reaches real exactness.

## Evidence

- `MagicMirror.Native\Mirror\MirrorEngine.cs`
- `MagicMirror.Native\Mirror\MirrorDrawable.cs`
- `MagicMirror.Native\Pages\MirrorOverlayPage.xaml.cs`
- `docs\releases\v1.0.5.md`
- `CHANGELOG.md`
- `docs\ARCHITECTURE.md`

## Verdict

FAIL for Phase-1 precision/honesty gate if exact linking is claimed. PASS only for block-level and measured OCR-line visual guidance.

## Vote-ready assertions

- Current code does not guarantee exact translated-word to source-word linkage.
- Current code provides useful block-level source/translation linkage.
- Public precision claims must stay scientifically limited.
