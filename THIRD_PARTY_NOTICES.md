# Third-party notices

Magic Mirror uses the .NET, .NET MAUI, ASP.NET Core, Windows App SDK, and
Cepha / WasmMvcRuntime ecosystems. Those components retain their own licenses
and support terms.

The repository also contains runtime DLL references under
`MagicMirror.Native/lib/` that are required for the native MAUI host to render
the Cepha MVC layer in-process. They are distributed as dependencies, not as
code owned by this repository.

OpenSans font assets under `MagicMirror.Native/Resources/Fonts/` retain their
original font license.

Optional OCR integration can call a local Tesseract installation. Tesseract and
traineddata files are not bundled in this repository; users may configure local
paths in the application settings.
