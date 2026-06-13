# Installation and operation

## Download release build

1. Open the latest release:
   <https://github.com/sbay-dev/MagicMirror/releases/latest>
2. Download `MagicMirror-v1.0.3-windows-x64.zip`.
3. Extract the ZIP to a writable folder.
4. Run `MagicMirror.Native.exe`.

If Windows SmartScreen appears, choose **More info** -> **Run anyway** only if
you downloaded the file from the official `sbay-dev/MagicMirror` release page.

## Requirements

- Windows 10 1809 or newer.
- .NET 10 runtime/SDK for framework-dependent builds.
- Windows OCR language packs for native OCR fallback.
- Optional: local Tesseract installation for `eng+ara` OCR.

## First run

1. Open the control window.
2. Choose target language, OCR mode, colors, and optional AI gateway.
3. Click **Open Mirror Overlay**.
4. Move the glass over a document or application.
5. Click **ترجم**.
6. Click a visible word to open/select dictionary context; drag the dictionary
   card away when it covers the source text.

## AI gateway

The native app posts to:

```text
{GatewayBaseUrl}/api/sarmad/ask
```

When `GatewayBaseUrl` is empty or unavailable, translation uses the no-key MT
fallback for continuity. Dictionary alternatives require a configured Sarmad
gateway and will report that the gateway is not configured instead of
fabricating results.

The old documentation gateway at `https://wmr-doc.pages.dev/api/sarmad/ask` is
not used as a default product fallback because it was deployed against the
deprecated `@cf/openai/gpt-oss-120b` model. Deploy this repository's web layer
with a supported model such as `@cf/openai/gpt-oss-20b`, then set
`GatewayBaseUrl` to that deployment.

## OCR

The pipeline prefers UI Automation text when the target window exposes it. If no
accessible text is available, it uses OCR:

- Tesseract, when `TesseractExePath` and `TessDataPath` are configured or
  discoverable.
- Windows.Media.Ocr as fallback.

## Logs

Runtime logs are written to the application data directory as `mirror.log`.
Use the log when reporting capture, OCR, translation, or dictionary failures.
