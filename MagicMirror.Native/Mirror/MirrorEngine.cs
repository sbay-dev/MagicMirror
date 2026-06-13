namespace MagicMirror.Native.Mirror;

/// <summary>
/// The end-to-end Magic Mirror pipeline: capture a screen region → OCR → font detection →
/// translation → a list of <see cref="TranslatedBlock"/>s positioned for overlay rendering.
/// </summary>
public sealed class MirrorEngine
{
    private readonly IScreenCapture _capture;
    private readonly OcrService _ocr;
    private readonly ITranslationService _translator;
    private readonly MirrorSettingsStore _settings;
    private readonly IWindowTextProvider _textProvider;

    public MirrorEngine(IScreenCapture capture, OcrService ocr, ITranslationService translator,
        MirrorSettingsStore settings, IWindowTextProvider textProvider)
    {
        _capture = capture;
        _ocr = ocr;
        _translator = translator;
        _settings = settings;
        _textProvider = textProvider;
    }

    public bool CaptureAvailable => _capture.IsAvailable;
    public bool WindowTextAvailable => _textProvider.IsAvailable;
    public ITranslationService Translator => _translator;

    /// <summary>Cheap downscaled capture for the live see-through preview (not used for OCR).</summary>
    public Task<CaptureResult> CapturePreviewAsync(int screenX, int screenY, int width, int height)
        => _capture.CapturePreviewAsync(screenX, screenY, width, height);

    /// <summary>
    /// Runs the full capture→OCR→translate flow for the given screen rectangle and returns
    /// the captured image plus the translated overlay blocks.
    /// </summary>
    public async Task<MirrorResult> TranslateRegionAsync(
        int screenX, int screenY, int width, int height, CancellationToken ct = default)
    {
        var settings = _settings.Current;
        MirrorLog.Info($"Translate region [{screenX},{screenY},{width}x{height}] target={settings.TargetLanguage} ocr={settings.OcrEngine}");
        var capture = await _capture.CaptureRegionAsync(screenX, screenY, width, height);
        if (capture.IsEmpty)
            return new MirrorResult { Capture = capture, Blocks = Array.Empty<TranslatedBlock>(), Status = "Capture failed" };
        MirrorLog.Info($"Captured {capture.Width}x{capture.Height} png={(capture.Png?.Length ?? 0)}B pixels={capture.Pixels.Length}B");

        // Prefer text straight from the window (UI Automation) — no OCR, lighter + more accurate.
        IReadOnlyList<OcrTextRegion> raw = Array.Empty<OcrTextRegion>();
        string method = "OCR";
        if (settings.UseWindowText && _textProvider.IsAvailable)
        {
            try
            {
                var uia = await _textProvider.ExtractTextAsync(screenX, screenY, width, height, ct);
                if (uia.Count > 0) { raw = uia; method = "UIA"; }
            }
            catch (Exception ex) { MirrorLog.Error("WindowText", ex); }
        }
        if (raw.Count == 0)
            raw = await _ocr.RecognizeAsync(capture, settings, ct);
        MirrorLog.Info($"Text via {method}: {raw.Count} regions");

        var filtered = raw
            .Where(r => !string.IsNullOrWhiteSpace(r.Text) && r.Text.Trim().Length >= 2
                        && (r.Confidence < 0 || r.Confidence >= 35) && r.Width >= 6 && r.Height >= 6)
            .ToList();

        // Merge fragments that sit on the same visual line into one reading-order line so the
        // translated (RTL) text is not scrambled across several little right-aligned boxes.
        var regions = MergeRows(filtered);
        if (method == "OCR")
            regions = MergeParagraphs(regions, capture.Width);
        MirrorLog.Info($"Merged rows={regions.Count} (from {filtered.Count} filtered)");
        if (regions.Count == 0)
            return new MirrorResult { Capture = capture, Blocks = Array.Empty<TranslatedBlock>(), Status = "No text found" };

        var sources = regions.Select(r => r.Text).ToList();
        var translations = await _translator.TranslateBatchAsync(sources, settings.TargetLanguage, settings, ct);
        MirrorLog.Info($"Translated {translations.Count}/{sources.Count}");

        var blocks = new List<TranslatedBlock>(regions.Count);
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            blocks.Add(new TranslatedBlock
            {
                OriginalText = r.Text,
                TranslatedText = i < translations.Count ? translations[i] : r.Text,
                X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                LineHeightHint = r.LineHeightHint,
                Confidence = r.Confidence,
                Font = FontMatcher.Detect(r.LineHeightHint > 0 ? r.LineHeightHint : r.Height, r.Text, settings.TargetLanguage),
            });
        }

        return new MirrorResult
        {
            Capture = capture,
            Blocks = blocks,
            Status = $"{blocks.Count} lines · {settings.TargetLanguage}",
        };
    }

    /// <summary>
    /// Groups OCR fragments that share a visual line (overlapping vertical centres) into a single
    /// reading-order line: fragments are ordered left→right, their text joined, and their boxes
    /// unioned. This stops the RTL overlay from scrambling text that OCR split into pieces.
    /// </summary>
    private static List<OcrTextRegion> MergeRows(List<OcrTextRegion> regions)
    {
        if (regions.Count == 0) return new List<OcrTextRegion>();

        var heights = regions.Select(r => r.Height).OrderBy(h => h).ToList();
        int medianH = heights[heights.Count / 2];
        double threshold = Math.Max(6, medianH * 0.6);

        var rows = new List<List<OcrTextRegion>>();
        foreach (var r in regions.OrderBy(r => r.Y + r.Height / 2.0))
        {
            double rc = r.Y + r.Height / 2.0;
            List<OcrTextRegion>? target = null;
            foreach (var row in rows)
            {
                double gc = row.Average(x => x.Y + x.Height / 2.0);
                if (Math.Abs(gc - rc) <= threshold) { target = row; break; }
            }
            if (target == null) rows.Add(new List<OcrTextRegion> { r });
            else target.Add(r);
        }

        var merged = new List<OcrTextRegion>(rows.Count);
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(r => r.X).ToList();
            int left = ordered.Min(r => r.X);
            int top = ordered.Min(r => r.Y);
            int right = ordered.Max(r => r.X + r.Width);
            int bottom = ordered.Max(r => r.Y + r.Height);
            string text = string.Join(" ", ordered.Select(r => r.Text.Trim()));
            var confs = ordered.Where(r => r.Confidence >= 0).Select(r => r.Confidence).ToList();
            merged.Add(new OcrTextRegion
            {
                Text = text,
                X = left, Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                LineHeightHint = Math.Max(1, ordered.Select(r => r.LineHeightHint > 0 ? r.LineHeightHint : r.Height).OrderBy(h => h).ElementAt(ordered.Count / 2)),
                Confidence = confs.Count > 0 ? confs.Average() : -1f,
            });
        }
        return merged.OrderBy(r => r.Y).ToList();
    }

    /// <summary>
    /// Reassembles OCR line rows into paragraph boxes for document-like captures. This is deliberately
    /// limited to OCR output (not UI Automation) and only activates when most rows are long, regular
    /// text lines; UI screens remain line-based. The paragraph keeps a line-height hint so typography
    /// uses the original body size rather than the full paragraph rectangle.
    /// </summary>
    private static List<OcrTextRegion> MergeParagraphs(List<OcrTextRegion> rows, int captureWidth)
    {
        if (!LooksDocumentLike(rows, captureWidth))
            return rows;

        var ordered = rows.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
        var heights = ordered.Select(r => r.LineHeightHint > 0 ? r.LineHeightHint : r.Height).OrderBy(h => h).ToList();
        int medianH = heights[heights.Count / 2];
        int leftTolerance = Math.Max(24, captureWidth / 18);
        int maxLineGap = Math.Max(8, (int)Math.Round(medianH * 1.55));

        var paragraphs = new List<List<OcrTextRegion>>();
        foreach (var row in ordered)
        {
            var lineH = row.LineHeightHint > 0 ? row.LineHeightHint : row.Height;
            bool rowIsBody = IsBodyParagraphRow(row, medianH);
            List<OcrTextRegion>? target = null;

            if (rowIsBody && paragraphs.Count > 0)
            {
                var previous = paragraphs[^1];
                var last = previous[^1];
                bool previousIsBody = previous.All(line => IsBodyParagraphRow(line, medianH));
                int lastBottom = last.Y + last.Height;
                bool closeVertically = row.Y - lastBottom <= maxLineGap;
                bool sameColumn = Math.Abs(row.X - previous[0].X) <= leftTolerance ||
                                  HorizontalOverlapRatio(row, previous[0]) >= 0.62;
                bool compatibleHeight = Math.Abs(lineH - medianH) <= medianH * 0.45;
                if (previousIsBody && closeVertically && sameColumn && compatibleHeight)
                    target = previous;
            }

            if (target == null)
                paragraphs.Add(new List<OcrTextRegion> { row });
            else
                target.Add(row);
        }

        return paragraphs.Select(MergeParagraph).OrderBy(r => r.Y).ThenBy(r => r.X).ToList();

        static OcrTextRegion MergeParagraph(List<OcrTextRegion> paragraph)
        {
            if (paragraph.Count == 1)
                return paragraph[0];

            var orderedLines = paragraph.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
            int left = orderedLines.Min(r => r.X);
            int top = orderedLines.Min(r => r.Y);
            int right = orderedLines.Max(r => r.X + r.Width);
            int bottom = orderedLines.Max(r => r.Y + r.Height);
            var lineHeights = orderedLines.Select(r => r.LineHeightHint > 0 ? r.LineHeightHint : r.Height).OrderBy(h => h).ToList();
            var confs = orderedLines.Where(r => r.Confidence >= 0).Select(r => r.Confidence).ToList();

            return new OcrTextRegion
            {
                Text = string.Join(" ", orderedLines.Select(r => r.Text.Trim())),
                X = left,
                Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                LineHeightHint = Math.Max(1, lineHeights[lineHeights.Count / 2]),
                Confidence = confs.Count > 0 ? confs.Average() : -1f,
            };
        }
    }

    private static bool LooksDocumentLike(List<OcrTextRegion> rows, int captureWidth)
    {
        if (rows.Count < 5 || captureWidth <= 0) return false;
        int longRows = rows.Count(r => r.Text.Length >= 18 && r.Width >= captureWidth * 0.22);
        int bodyRows = rows.Count(r => r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 5);
        return longRows >= rows.Count * 0.45 && bodyRows >= rows.Count * 0.45;
    }

    private static bool IsBodyParagraphRow(OcrTextRegion row, int medianLineHeight)
    {
        var words = row.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var lineH = row.LineHeightHint > 0 ? row.LineHeightHint : row.Height;
        if (words < 5) return false;
        if (lineH > medianLineHeight * 1.28) return false;

        var text = row.Text.Trim();
        if (text.EndsWith(":", StringComparison.Ordinal) && words <= 8) return false;
        return true;
    }

    private static double HorizontalOverlapRatio(OcrTextRegion a, OcrTextRegion b)
    {
        int left = Math.Max(a.X, b.X);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int overlap = Math.Max(0, right - left);
        return overlap / (double)Math.Max(1, Math.Min(a.Width, b.Width));
    }
}

/// <summary>Output of a mirror translation pass.</summary>
public sealed class MirrorResult
{
    public required CaptureResult Capture { get; init; }
    public required IReadOnlyList<TranslatedBlock> Blocks { get; init; }
    public string Status { get; init; } = "";
}
