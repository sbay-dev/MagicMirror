# Release process

This repository publishes source code to GitHub and uploads runnable Windows
ZIP packages to GitHub Releases.

## Verify

```powershell
.\scripts\verify.ps1
```

## Package Windows build

```powershell
.\scripts\package-windows.ps1 -Version 1.0.5
```

Output:

```text
artifacts\MagicMirror-v1.0.5-windows-x64.zip
```

## Publish release

```powershell
gh release create v1.0.5 `
  artifacts\MagicMirror-v1.0.5-windows-x64.zip `
  --title "Magic Mirror v1.0.5" `
  --notes-file docs\releases\v1.0.5.md
```

If `v1.0.5` already exists, upload the rebuilt asset with:

```powershell
gh release upload v1.0.5 `
  artifacts\MagicMirror-v1.0.5-windows-x64.zip `
  --clobber
```
