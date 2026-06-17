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

    /// <summary>Full-resolution capture used after manipulation ends so the settled mirror is crisp.</summary>
    public Task<CaptureResult> CaptureSettledPreviewAsync(int screenX, int screenY, int width, int height)
        => _capture.CaptureRegionAsync(screenX, screenY, width, height);

    /// <summary>
    /// Runs the full capture→OCR→translate flow for the given screen rectangle and returns
    /// the captured image plus the translated overlay blocks.
    /// </summary>
    public async Task<MirrorResult> TranslateRegionAsync(
        int screenX, int screenY, int width, int height, CancellationToken ct = default)
        => await TranslateRegionAsync(screenX, screenY, width, height, settingsOverride: null, ct);

    public async Task<MirrorResult> TranslateRegionAsync(
        int screenX, int screenY, int width, int height, MirrorSettings? settingsOverride, CancellationToken ct = default)
    {
        var settings = settingsOverride ?? _settings.Current;
        var prepared = await PrepareTranslationRegionsAsync(screenX, screenY, width, height, settings, ct);
        var capture = prepared.Capture;
        var regions = prepared.Regions;
        if (capture.IsEmpty)
            return new MirrorResult { Capture = capture, Blocks = Array.Empty<TranslatedBlock>(), Status = "Capture failed" };
        if (regions.Count == 0)
            return new MirrorResult { Capture = capture, Blocks = Array.Empty<TranslatedBlock>(), Status = "No text found" };

        var sources = regions.Select(r => r.Text).ToList();
        var translationResult = await _translator.TranslateBatchAsync(sources, settings.TargetLanguage, settings, ct);
        var translations = translationResult.Lines;
        MirrorLog.Info($"Translated {translations.Count}/{sources.Count}");

        return new MirrorResult
        {
            Capture = capture,
            Blocks = BuildBlocks(
                regions,
                translations,
                settings,
                capture,
                height,
                translationResult.LineSources,
                translationResult.LineSourceLabels),
            Status = $"{regions.Count} lines · {settings.TargetLanguage} · {translationResult.SourceLabel}",
            TranslationSource = translationResult.Source,
            TranslationSourceLabel = translationResult.SourceLabel,
        };
    }

    public async IAsyncEnumerable<MirrorResult> TranslateRegionProgressiveAsync(
        int screenX,
        int screenY,
        int width,
        int height,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var result in TranslateRegionProgressiveAsync(screenX, screenY, width, height, settingsOverride: null, ct))
            yield return result;
    }

    public async IAsyncEnumerable<MirrorResult> TranslateRegionProgressiveAsync(
        int screenX,
        int screenY,
        int width,
        int height,
        MirrorSettings? settingsOverride,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var settings = settingsOverride ?? _settings.Current;
        var prepared = await PrepareTranslationRegionsAsync(screenX, screenY, width, height, settings, ct);
        var capture = prepared.Capture;
        var regions = prepared.Regions;
        if (capture.IsEmpty)
        {
            yield return new MirrorResult { Capture = capture, Blocks = Array.Empty<TranslatedBlock>(), Status = "Capture failed" };
            yield break;
        }
        if (regions.Count == 0)
        {
            yield return new MirrorResult { Capture = capture, Blocks = Array.Empty<TranslatedBlock>(), Status = "No text found" };
            yield break;
        }

        var sources = regions.Select(r => r.Text).ToList();
        var translations = Enumerable.Repeat("…", sources.Count).ToArray();
        yield return new MirrorResult
        {
            Capture = capture,
            Blocks = BuildBlocks(regions, translations, settings, capture, height),
            Status = $"0/{sources.Count} lines · streaming…",
            TranslationSource = TranslationSourceKind.OriginalTextFallback,
            TranslationSourceLabel = "streaming",
        };

        var sourceKinds = Enumerable.Repeat(TranslationSourceKind.OriginalTextFallback, sources.Count).ToArray();
        var sourceLabels = Enumerable.Repeat("pending context-aware translation", sources.Count).ToArray();
        await foreach (var progress in _translator.TranslateBatchProgressiveAsync(sources, settings.TargetLanguage, settings, ct))
        {
            for (var i = 0; i < progress.Lines.Count && progress.StartIndex + i < translations.Length; i++)
            {
                translations[progress.StartIndex + i] = string.IsNullOrWhiteSpace(progress.Lines[i])
                    ? sources[progress.StartIndex + i]
                    : progress.Lines[i];
                sourceKinds[progress.StartIndex + i] = i < progress.LineSources.Count
                    ? progress.LineSources[i]
                    : progress.Source;
                sourceLabels[progress.StartIndex + i] = i < progress.LineSourceLabels.Count
                    ? progress.LineSourceLabels[i]
                    : progress.SourceLabel;
            }

            var source = SummarizeSources(sourceKinds.Take(progress.Completed).ToList());
            var label = TranslationSourceSummary(sourceKinds.Take(progress.Completed).ToList());
            yield return new MirrorResult
            {
                Capture = capture,
                Blocks = BuildBlocks(
                    regions,
                    translations,
                    settings,
                    capture,
                    height,
                    sourceKinds,
                    sourceLabels),
                Status = $"{progress.Completed}/{sources.Count} lines · {settings.TargetLanguage} · {label}",
                TranslationSource = source,
                TranslationSourceLabel = label,
            };
        }
    }

    private async Task<(CaptureResult Capture, IReadOnlyList<OcrTextRegion> Regions)> PrepareTranslationRegionsAsync(
        int screenX,
        int screenY,
        int width,
        int height,
        MirrorSettings settings,
        CancellationToken ct)
    {
        MirrorLog.Info($"Translate region [{screenX},{screenY},{width}x{height}] target={settings.TargetLanguage} ocr={settings.OcrEngine}");
        var capture = await _capture.CaptureRegionAsync(screenX, screenY, width, height, settings.OcrCaptureScale);
        if (capture.IsEmpty)
            return (capture, Array.Empty<OcrTextRegion>());
        MirrorLog.Info($"Captured {capture.Width}x{capture.Height} png={(capture.Png?.Length ?? 0)}B pixels={capture.Pixels.Length}B");

        IReadOnlyList<OcrTextRegion> raw = Array.Empty<OcrTextRegion>();
        string method = "OCR";
        if (settings.UseWindowText && _textProvider.IsAvailable)
        {
            try
            {
                var uia = await _textProvider.ExtractTextAsync(screenX, screenY, width, height, ct);
                uia = ScaleTextRegionsForCapture(uia, capture, width, height);
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

        var regions = MergeRows(filtered, capture.Width);
        MirrorLog.Info($"Merged rows={regions.Count} (from {filtered.Count} filtered)");
        return (capture, regions);
    }

    private static IReadOnlyList<OcrTextRegion> ScaleTextRegionsForCapture(
        IReadOnlyList<OcrTextRegion> regions,
        CaptureResult capture,
        int sourceWidth,
        int sourceHeight)
    {
        if (regions.Count == 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return regions;

        var xScale = capture.Width / (double)sourceWidth;
        var yScale = capture.Height / (double)sourceHeight;
        if (Math.Abs(xScale - 1.0) < 0.01 && Math.Abs(yScale - 1.0) < 0.01)
            return regions;

        return regions.Select(r => new OcrTextRegion
        {
            Text = r.Text,
            X = (int)Math.Round(r.X * xScale),
            Y = (int)Math.Round(r.Y * yScale),
            Width = Math.Max(1, (int)Math.Round(r.Width * xScale)),
            Height = Math.Max(1, (int)Math.Round(r.Height * yScale)),
            LineHeightHint = r.LineHeightHint > 0
                ? Math.Max(1, (int)Math.Round(r.LineHeightHint * yScale))
                : 0,
            Confidence = r.Confidence,
            SourceLanguage = r.SourceLanguage,
            SourceLines = ScaleSourceLines(r.SourceLines, xScale, yScale),
        }).ToList();
    }

    private static IReadOnlyList<OcrTextRegion> ScaleSourceLines(IReadOnlyList<OcrTextRegion> sourceLines, double xScale, double yScale)
        => sourceLines.Count == 0
            ? Array.Empty<OcrTextRegion>()
            : sourceLines.Select(r => new OcrTextRegion
            {
                Text = r.Text,
                X = (int)Math.Round(r.X * xScale),
                Y = (int)Math.Round(r.Y * yScale),
                Width = Math.Max(1, (int)Math.Round(r.Width * xScale)),
                Height = Math.Max(1, (int)Math.Round(r.Height * yScale)),
                LineHeightHint = r.LineHeightHint > 0
                    ? Math.Max(1, (int)Math.Round(r.LineHeightHint * yScale))
                    : 0,
                Confidence = r.Confidence,
                SourceLanguage = r.SourceLanguage,
            }).ToList();

    private static IReadOnlyList<TranslatedBlock> BuildBlocks(
        IReadOnlyList<OcrTextRegion> regions,
        IReadOnlyList<string> translations,
        MirrorSettings settings,
        CaptureResult capture,
        int sourceHeight,
        IReadOnlyList<TranslationSourceKind>? translationSources = null,
        IReadOnlyList<string>? translationSourceLabels = null)
    {
        var blocks = new List<TranslatedBlock>(regions.Count);
        var yScale = sourceHeight > 0 ? Math.Max(0.01, capture.Height / (double)sourceHeight) : 1.0;
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            var typographicHeight = Math.Max(1, (int)Math.Round((r.LineHeightHint > 0 ? r.LineHeightHint : r.Height) / yScale));
            blocks.Add(new TranslatedBlock
            {
                OriginalText = r.Text,
                TranslatedText = i < translations.Count ? translations[i] : r.Text,
                X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                LineHeightHint = r.LineHeightHint,
                Confidence = r.Confidence,
                SourceLines = SourceLinesForBlock(r),
                TranslationSource = translationSources != null && i < translationSources.Count
                    ? translationSources[i]
                    : TranslationSourceKind.OriginalTextFallback,
                TranslationSourceLabel = translationSourceLabels != null && i < translationSourceLabels.Count
                    ? translationSourceLabels[i]
                    : "",
                Font = FontMatcher.Detect(typographicHeight, r.Text, settings.TargetLanguage),
            });
        }

        return blocks;
    }

    private static IReadOnlyList<OcrTextRegion> SourceLinesForBlock(OcrTextRegion region)
    {
        var sourceLines = region.SourceLines.Count > 0
            ? region.SourceLines
            : new[] { region };
        return sourceLines.Select(CloneSourceLine).ToList();
    }

    private static OcrTextRegion CloneSourceLine(OcrTextRegion region) => new()
    {
        Text = region.Text,
        X = region.X,
        Y = region.Y,
        Width = region.Width,
        Height = region.Height,
        LineHeightHint = region.LineHeightHint,
        Confidence = region.Confidence,
        SourceLanguage = region.SourceLanguage,
    };

    private static TranslationSourceKind SummarizeSources(IReadOnlyList<TranslationSourceKind> sources)
    {
        var distinct = sources.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : TranslationSourceKind.Mixed;
    }

    private static string TranslationSourceSummary(IReadOnlyList<TranslationSourceKind> sources)
    {
        var source = SummarizeSources(sources);
        if (source != TranslationSourceKind.Mixed)
            return SourceLabel(source);

        var parts = sources
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{SourceLabel(g.Key)}={g.Count()}");
        return "mixed sources: " + string.Join(", ", parts);
    }

    private static string SourceLabel(TranslationSourceKind source) => source switch
    {
        TranslationSourceKind.SarmadGateway => "Sarmad",
        TranslationSourceKind.MachineTranslationFallback => "MT",
        TranslationSourceKind.OriginalTextFallback => "Original",
        _ => "Mixed",
    };

    /// <summary>
    /// Groups OCR fragments that share a visual line (overlapping vertical centres) into a single
    /// reading-order line: fragments are ordered left→right, their text joined, and their boxes
    /// unioned. This stops the RTL overlay from scrambling text that OCR split into pieces.
    /// </summary>
    private static List<OcrTextRegion> MergeRows(List<OcrTextRegion> regions, int captureWidth)
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
            foreach (var segment in SplitRowIntoColumnSegments(ordered, medianH, captureWidth))
                merged.Add(MergeRowSegment(segment));
        }
        return merged.OrderBy(r => r.Y).ToList();

        static OcrTextRegion MergeRowSegment(List<OcrTextRegion> ordered)
        {
            int left = ordered.Min(r => r.X);
            int top = ordered.Min(r => r.Y);
            int right = ordered.Max(r => r.X + r.Width);
            int bottom = ordered.Max(r => r.Y + r.Height);
            string text = string.Join(" ", ordered.Select(r => r.Text.Trim()));
            var confs = ordered.Where(r => r.Confidence >= 0).Select(r => r.Confidence).ToList();
            var lineHeightHint = Math.Max(1, ordered.Select(r => r.LineHeightHint > 0 ? r.LineHeightHint : r.Height).OrderBy(h => h).ElementAt(ordered.Count / 2));
            var confidence = confs.Count > 0 ? confs.Average() : -1f;
            var sourceLine = new OcrTextRegion
            {
                Text = text,
                X = left, Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                LineHeightHint = lineHeightHint,
                Confidence = confidence,
            };
            return new OcrTextRegion
            {
                Text = text,
                X = left, Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                LineHeightHint = lineHeightHint,
                Confidence = confidence,
                SourceLines = new[] { sourceLine },
            };
        }
    }

    private static IEnumerable<List<OcrTextRegion>> SplitRowIntoColumnSegments(
        List<OcrTextRegion> ordered,
        int medianLineHeight,
        int captureWidth)
    {
        if (ordered.Count <= 1)
        {
            yield return ordered;
            yield break;
        }

        var maxInlineGap = Math.Max(medianLineHeight * 5, captureWidth > 0 ? captureWidth / 18 : 80);
        var current = new List<OcrTextRegion> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = current[^1];
            var gap = ordered[i].X - (previous.X + previous.Width);
            if (gap > maxInlineGap)
            {
                yield return current;
                current = new List<OcrTextRegion>();
            }

            current.Add(ordered[i]);
        }

        if (current.Count > 0)
            yield return current;
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
                SourceLines = orderedLines.SelectMany(SourceLinesForBlock).ToList(),
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
    public TranslationSourceKind TranslationSource { get; init; } = TranslationSourceKind.OriginalTextFallback;
    public string TranslationSourceLabel { get; init; } = "";
}
