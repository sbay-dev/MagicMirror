# Magic Mirror - المرآة السحرية

Magic Mirror is a Windows-first transparent overlay that turns any visible
document or application into a translated reader surface. Place the glass over a
window, translate the text behind it, and read the result in the target language
while preserving document structure, typography, and reading direction.

[Download latest release](https://github.com/sbay-dev/MagicMirror/releases/latest)
· [Installation](docs/INSTALLATION.md)
· [Cloudflare gateway](docs/CLOUDFLARE_GATEWAY.md)
· [User guide](docs/USER_GUIDE.md)
· [Architecture](docs/ARCHITECTURE.md)
· [Specifications](docs/specifications/AUTHOR-LINGUISTIC-SPECIFICATIONS.md)

## What it does

- Transparent always-on-top MAUI overlay for Windows.
- Captures text behind the overlay while excluding the mirror itself from
  capture.
- Reads exposed window text through UI Automation when available.
- Falls back to OCR through Tesseract or Windows.Media.Ocr.
- Translates through a deployed Sarmad / Cloudflare `@cf/openai/gpt-oss-20b` gateway,
  with machine-translation fallback for continuity.
- Renders Arabic-first document output with robust RTL/LTR handling for
  acronyms and scientific terms such as `CNS`, `LCNS`, and `QKV`.
- Provides reader-page mode, configurable background/ink colors, copy actions,
  scrolling, live preview, and direct click-to-select dictionary analysis.

## Quick start from release

1. Download `MagicMirror-v1.0.3-windows-x64.zip` from
   <https://github.com/sbay-dev/MagicMirror/releases/latest>.
2. Extract the ZIP.
3. Run `MagicMirror.Native.exe`.
4. Open the overlay, place it over text, and press **ترجم**.

See [docs/INSTALLATION.md](docs/INSTALLATION.md) for requirements and OCR/AI
configuration.

## Build from source

Requirements:

- Windows 10 1809 or newer.
- .NET SDK 10.0.300 or newer compatible SDK.
- .NET MAUI workload for Windows.
- Cepha / NetWasmMvc.SDK package source configured.

```powershell
git clone https://github.com/sbay-dev/MagicMirror.git
cd MagicMirror
.\scripts\verify.ps1
```

Run the native app:

```powershell
dotnet run --project .\MagicMirror.Native\MagicMirror.Native.csproj `
  -f net10.0-windows10.0.19041.0
```

Create a release ZIP:

```powershell
.\scripts\package-windows.ps1 -Version 1.0.3
```

## Repository layout

```text
MagicMirror/          Cepha / NetWasmMvc.SDK web layer and AI gateway contract
MagicMirror.Native/   MAUI native host, capture, OCR, translation, overlay UI
docs/                 Installation, architecture, release process, specs
scripts/              Verification and packaging scripts
```

## Privacy and security

Magic Mirror captures only the overlay rectangle selected by the user. AI
translation requests are sent to the configured Sarmad gateway. Do not use an
untrusted gateway for sensitive documents. See [SECURITY.md](SECURITY.md).

## License

MIT for repository source code. Third-party components retain their own
licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
