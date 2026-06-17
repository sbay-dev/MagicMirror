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

        var pixels = capture.Pixels;
        var ocrWidth = capture.Width;
        var ocrHeight = capture.Height;
        var coordinateXScale = 1.0;
        var coordinateYScale = 1.0;
        var longest = Math.Max(capture.Width, capture.Height);
        if (longest > OcrEngine.MaxImageDimension)
        {
            var scale = OcrEngine.MaxImageDimension / (double)longest;
            ocrWidth = Math.Max(1, (int)Math.Round(capture.Width * scale));
            ocrHeight = Math.Max(1, (int)Math.Round(capture.Height * scale));
            pixels = ResizeBgraNearest(capture.Pixels, capture.Width, capture.Height, ocrWidth, ocrHeight);
            coordinateXScale = capture.Width / (double)ocrWidth;
            coordinateYScale = capture.Height / (double)ocrHeight;
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, ocrWidth, ocrHeight, BitmapAlphaMode.Ignore);

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
            int height = Math.Max(1, (int)Math.Ceiling((b - t) * coordinateYScale));
            regions.Add(new OcrTextRegion
            {
                Text = line.Text,
                X = (int)Math.Floor(l * coordinateXScale),
                Y = (int)Math.Floor(t * coordinateYScale),
                Width = Math.Max(1, (int)Math.Ceiling((r - l) * coordinateXScale)),
                Height = height,
                LineHeightHint = height,
                Confidence = -1f, // engine does not expose per-line confidence
            });
        }
        return regions;
    }

    private static byte[] ResizeBgraNearest(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var target = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, (int)Math.Round((y + 0.5) * sourceHeight / targetHeight - 0.5));
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, (int)Math.Round((x + 0.5) * sourceWidth / targetWidth - 0.5));
                var si = (sourceY * sourceWidth + sourceX) * 4;
                var di = (y * targetWidth + x) * 4;
                target[di] = source[si];
                target[di + 1] = source[si + 1];
                target[di + 2] = source[si + 2];
                target[di + 3] = 255;
            }
        }

        return target;
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
