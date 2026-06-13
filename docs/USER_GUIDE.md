# User guide

## Overlay controls

| Control | Purpose |
| --- | --- |
| `A-` / `A+` | Decrease/increase translated text size. |
| `مطابق` / `مقروء` / `موحد` | Cycle layout mode. |
| `اصل` | Copy captured original text. |
| `ترجمة` | Copy translated text. |
| `ترجم` | Capture, OCR/read text, translate, and render overlay. |
| `↑` / `↓` or mouse wheel | Scroll translated overflow. |
| `👁` | Toggle live preview. |
| `▣ خلفية` | Cycle translated background color. |
| `◌ %` | Cycle translated background opacity. 100% becomes reader-page mode. |
| `حبر` | Cycle translated text color. |

## Dictionary workflow

1. Translate a document.
2. Click any visible word or line in the mirror.
3. The selected term is highlighted.
4. Drag the dictionary card if it covers the source text.
5. Click **معجم** to request domain-aware lexical analysis.

The dictionary prompt asks the model to classify the document domain, provide at
least five alternatives, explain fit/non-fit contexts, and end with a decisive
summary. If the AI gateway is unavailable, the panel reports the exact failure
instead of inventing alternatives.
