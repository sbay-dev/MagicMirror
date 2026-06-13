namespace MagicMirror.Native.Mirror;

/// <summary>
/// Chooses and runs OCR engines according to <see cref="MirrorSettings.OcrEngine"/>:
/// Tesseract first (project primary) with the OS engine as fallback. Engines are supplied
/// by DI; on platforms without a native engine only Tesseract is present.
/// </summary>
public sealed class OcrService
{
    private readonly IReadOnlyList<IOcrEngine> _engines;

    public OcrService(IEnumerable<IOcrEngine> engines) => _engines = engines.ToList();

    public async Task<IReadOnlyList<OcrTextRegion>> RecognizeAsync(
        CaptureResult capture, MirrorSettings settings, CancellationToken ct = default)
    {
        foreach (var engine in OrderEngines(settings))
        {
            bool available;
            try { available = await engine.IsAvailableAsync(); } catch { available = false; }
            if (!available) continue;

            try
            {
                var regions = await engine.RecognizeAsync(capture, settings, ct);
                if (regions.Count > 0) return regions;
            }
            catch { /* try next engine */ }
        }
        return Array.Empty<OcrTextRegion>();
    }

    /// <summary>The engine actually used last time (for status display).</summary>
    public string? LastEngineName { get; private set; }

    private IEnumerable<IOcrEngine> OrderEngines(MirrorSettings s)
    {
        var tesseract = _engines.FirstOrDefault(e => e.Name == "Tesseract");
        var native = _engines.Where(e => e.Name != "Tesseract").ToList();

        switch (s.OcrEngine)
        {
            case OcrEnginePreference.NativeOnly:
                foreach (var n in native) yield return n;
                break;
            case OcrEnginePreference.TesseractOnly:
                if (tesseract != null) yield return tesseract;
                break;
            default: // TesseractThenNative
                if (tesseract != null) yield return tesseract;
                foreach (var n in native) yield return n;
                break;
        }
    }
}
