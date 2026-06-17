# User guide

## Overlay controls

| Control | Purpose |
| --- | --- |
| `A-` / `A+` | Decrease/increase translated text size. |
| `مطابق` / `مقروء` / `موحد` | Cycle layout mode. |
| `اصل` | Copy captured original text. |
| `ترجمة` | Copy translated text. |
| `ترجم` | Capture, OCR/read text, translate, and render overlay. |
| `↑` / `↓` or mouse wheel | Scroll translated overflow; use `Ctrl` + wheel to resize while translated. |
| `👁` | Toggle live preview. |
| `▣ خلفية` | Cycle translated background color. |
| `◌ %` | Cycle translated background opacity. 100% becomes reader-page mode. |
| `حبر` | Cycle translated text color. |
| `القارئ` / `فصل القارئ` | Open a detachable reader window with its own text size, wrapping, background, opacity, ink, copy, and dictionary controls. |
| `معجم المحدد` | Send the selected native-reader/editor text to the dictionary workflow and mark its source OCR block. |

## Dictionary workflow

1. Translate a document.
2. Select text in the transparent mirror editor or detached reader, or click a
   visible translated/source hit region.
3. The selected term is highlighted and the corresponding OCR/source block is
   marked on the original capture when measured provenance is available.
4. Right-click and choose **معجم المحدد**, or use the visible dictionary button.
5. Use the **المعجم** tab for the linguistic answer and **التوثيق التقني** for
   OCR rectangles, hashes, local Merkle root/path, and translation-source proof.
6. Drag the dictionary card if it covers the source text.

The dictionary prompt asks the model to classify the document domain, provide at
least five alternatives, explain fit/non-fit contexts, and end with a decisive
summary. It is instructed to act as a provenance auditor: do not treat MT or
original fallback text as a verified Sarmad glossary path. If the AI gateway is
unavailable, the panel reports the exact failure instead of inventing
alternatives.

## Academic/confidential use

For publisher-style or confidential manuscripts, keep the Sarmad gateway
configured and decline the MT prompt unless you explicitly accept sending text
to Google `gtx` / MyMemory. MT fallback is not executed by default, is labelled
in the overlay when used, applies to the whole capture after confirmation, and
is not equivalent to the domain-aware academic AI translation path.
