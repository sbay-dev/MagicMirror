# Contributing

Magic Mirror has two layers:

1. `MagicMirror/` - Cepha / NetWasmMvc.SDK web layer. Keep the Cepha worker
   sovereignty model: controllers, routing, models, and Razor views run in the
   .NET Web Worker; the main thread remains a display surface.
2. `MagicMirror.Native/` - .NET MAUI native host and overlay.

Before submitting changes:

```powershell
.\scripts\verify.ps1
```

Guidelines:

- Preserve the Worker -> Main thread boundary in the Cepha app.
- Do not modify generated/runtime files such as `main.js`,
  `cepha-runtime-worker.js`, or `cepha-data-worker.js` unless the frame-buffer
  protocol is the actual subject of the change.
- Keep capture/OCR/translation failures explicit in logs; do not silently fake
  AI dictionary results.
- Keep RTL/LTR rendering and document typography behavior covered when changing
  layout code.
