# Phase 1 Thesis - Code Risk

Reviewer: ec-code-risk
Model: gpt-5.5
Mode: code review with follow-up re-review

## Scope

Release-grade risks in overlay rendering, input handling, OCR/capture, translation fallback behavior, packaging/release docs, and overclaims in the current unstaged Magic Mirror v1.0.5 changes.

## Method

The reviewer inspected unstaged diffs and relevant code, built the solution, checked release/privacy docs, then re-reviewed the two reported blockers after fixes.

## Findings

- Initial blocker: `AllowMachineTranslationFallback` defaulted on, so captured text could be sent to Google gtx / MyMemory by default, while privacy docs omitted this path.
- Initial blocker: dictionary `Apply` could corrupt visible translation during active typewriter reveal because `_typewriterTargets` was not synchronized with `_selectedDictionaryBlock.TranslatedText`.
- Fix re-review result: PASS. MT fallback is now opt-in/off by default, MT calls are gated, README/SECURITY/INSTALLATION disclose Google/MyMemory, and dictionary apply updates both visible translation and the typewriter target with rollback.
- Build passed after the relevant fixes.

## Blockers

No remaining blocker for the two re-reviewed code-risk items. Broader publisher-grade blockers remain outside this re-review scope.

## Evidence

- `MagicMirror.Native\Mirror\MirrorModels.cs`
- `MagicMirror.Native\Mirror\SarmadTranslationService.cs`
- `MagicMirror.Native\Pages\MirrorOverlayPage.xaml.cs`
- `README.md`
- `SECURITY.md`
- `docs\INSTALLATION.md`

## Verdict

PASS for the targeted privacy/default and typewriter/dictionary synchronization fixes. This does not certify publisher-grade readiness.

## Vote-ready assertions

- MT fallback default/privacy blocker is fixed.
- Dictionary apply plus active typewriter blocker is fixed.
- Remaining council decision must still reject publisher-grade claims until non-code quality gates are met.
