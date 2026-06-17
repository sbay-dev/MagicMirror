# FINAL — Expert Council Consensus Verdict

- Topic source: `topic.txt`
- Merkle root: `27715715f4236048bc9a1dc78859626cd0c66281e143752a381249cf0966a7dc`
- Members: 6
- Adoption threshold: 4 AGREE votes
- Sealed UTC: 2026-06-14T22:21:14.7177591Z

## Slate outcome

| ID | Text | Agree | Disagree | Abstain | Status |
|---|---|---:|---:|---:|---|
| A1 | Magic Mirror must not be marketed as publisher-grade, publisher-adopted, or certified for academic publishing in v1.0.5. | 6 | 0 | 0 | ADOPTED |
| A2 | Magic Mirror v1.0.5 may proceed only as a personal/research academic reading-aid preview with explicit limitations and user visual approval. | 6 | 0 | 0 | ADOPTED |
| A3 | Third-party MT fallback must stay opt-in, off by default, visibly labelled non-academic, and documented as sending text to Google gtx / MyMemory. | 6 | 0 | 0 | ADOPTED |
| A4 | Tesseract tessdata must be portable or auto-discovered; developer-only absolute paths are not acceptable release defaults. | 6 | 0 | 0 | ADOPTED |
| A5 | Source-linking claims must be limited to block/measured OCR-line provenance; no exact source-word or publisher-precision claim is allowed. | 6 | 0 | 0 | ADOPTED |
| A6 | Professional publisher release remains blocked until automated tests, translation-quality evaluation, structured export, accessibility/compliance, and external validation exist. | 6 | 0 | 0 | ADOPTED |
| A7 | The rebuilt v1.0.5 Windows artifact may be treated as a local review package, not a certified public publisher release. | 6 | 0 | 0 | ADOPTED |

## Adopted assertions

- **A1** — Magic Mirror must not be marketed as publisher-grade, publisher-adopted, or certified for academic publishing in v1.0.5.
- **A2** — Magic Mirror v1.0.5 may proceed only as a personal/research academic reading-aid preview with explicit limitations and user visual approval.
- **A3** — Third-party MT fallback must stay opt-in, off by default, visibly labelled non-academic, and documented as sending text to Google gtx / MyMemory.
- **A4** — Tesseract tessdata must be portable or auto-discovered; developer-only absolute paths are not acceptable release defaults.
- **A5** — Source-linking claims must be limited to block/measured OCR-line provenance; no exact source-word or publisher-precision claim is allowed.
- **A6** — Professional publisher release remains blocked until automated tests, translation-quality evaluation, structured export, accessibility/compliance, and external validation exist.
- **A7** — The rebuilt v1.0.5 Windows artifact may be treated as a local review package, not a certified public publisher release.

## Verification

```powershell
council verify -CouncilRoot <this-folder>
```