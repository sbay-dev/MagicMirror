using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using MagicMirror.Native.Mirror;

namespace MagicMirror.Native.Platforms.Windows;

/// <summary>
/// Fallback OCR using the built-in <c>Windows.Media.Ocr</c> engine. Works out of the box for
/// languages whose OCR pack is installed (English ships by default); used when Tesseract is
/// not resolvable. Returns line-level regions (union of the line's word boxes) so the
/// translator receives whole phrases.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    public string Name => "Windows.Media.Ocr";

    public Task<bool> IsAvailableAsync()
        => Task.FromResult(OcrEngine.AvailableRecognizerLanguages.Count > 0);

    public async Task<IReadOnlyList<OcrTextRegion>> RecognizeAsync(
        CaptureResult capture, MirrorSettings settings, CancellationToken ct = default)
    {
        if (capture.IsEmpty || capture.Pixels.Length == 0) return Array.Empty<OcrTextRegion>();

        // Guard against a buffer/stride mismatch before handing raw memory to WinRT.
        long expected = (long)capture.Width * capture.Height * 4;
        if (capture.Pixels.Length < expected) return Array.Empty<OcrTextRegion>();

        var engine = ResolveEngine(settings.SourceLanguageHint);
        if (engine == null) return Array.Empty<OcrTextRegion>();

        // Windows.Media.Ocr caps the input dimension; bail if exceeded (Tesseract handles big frames).
        if (capture.Width > OcrEngine.MaxImageDimension || capture.Height > OcrEngine.MaxImageDimension)
            return Array.Empty<OcrTextRegion>();

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            capture.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, capture.Width, capture.Height, BitmapAlphaMode.Ignore);

        var result = await engine.RecognizeAsync(bitmap);
        if (result?.Lines == null) return Array.Empty<OcrTextRegion>();

        var regions = new List<OcrTextRegion>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var word in line.Words)
            {
                var bb = word.BoundingRect;
                l = Math.Min(l, bb.Left);
                t = Math.Min(t, bb.Top);
                r = Math.Max(r, bb.Right);
                b = Math.Max(b, bb.Bottom);
            }
            int height = Math.Max(1, (int)Math.Ceiling(b - t));
            regions.Add(new OcrTextRegion
            {
                Text = line.Text,
                X = (int)Math.Floor(l),
                Y = (int)Math.Floor(t),
                Width = Math.Max(1, (int)Math.Ceiling(r - l)),
                Height = height,
                LineHeightHint = height,
                Confidence = -1f, // engine does not expose per-line confidence
            });
        }
        return regions;
    }

    private static OcrEngine? ResolveEngine(string? sourceHint)
    {
        if (!string.IsNullOrWhiteSpace(sourceHint) &&
            !sourceHint.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fromHint = OcrEngine.TryCreateFromLanguage(new Language(sourceHint));
                if (fromHint != null) return fromHint;
            }
            catch { }
        }

        var fromProfile = OcrEngine.TryCreateFromUserProfileLanguages();
        if (fromProfile != null) return fromProfile;

        var langs = OcrEngine.AvailableRecognizerLanguages;
        return langs.Count > 0 ? OcrEngine.TryCreateFromLanguage(langs[0]) : null;
    }
}
