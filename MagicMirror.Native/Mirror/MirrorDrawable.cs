using Microsoft.Maui.Graphics;
using System.Text.RegularExpressions;
using IImage = Microsoft.Maui.Graphics.IImage;
using Font = Microsoft.Maui.Graphics.Font;

namespace MagicMirror.Native.Mirror;

/// <summary>
/// Paints the mirror glass: the live/frozen capture of what is behind the window, an optional
/// dimming layer, and each translated block positioned over its original text box so the
/// translation sits on top of the source with matched size/weight.
///
/// Coordinate model: block boxes are in capture-pixel space; the drawable scales them to the
/// GraphicsView canvas (DIPs) using the capture/​canvas ratio, so alignment holds across DPI.
/// </summary>
public sealed class MirrorDrawable : IDrawable
{
    public sealed record TextHitRegion(TranslatedBlock Block, string Text, RectF Bounds, bool IsWord);

    public IImage? Background { get; set; }
    public int CaptureWidth { get; set; }
    public int CaptureHeight { get; set; }
    public IReadOnlyList<TranslatedBlock> Blocks { get; set; } = Array.Empty<TranslatedBlock>();

    /// <summary>0..1 opacity of the dimming layer applied when translations are shown.</summary>
    public double Dim { get; set; } = 0.45;

    /// <summary>Document-color halo/background painted behind translated glyphs.</summary>
    public Color TranslationBackgroundColor { get; set; } =
        MirrorAppearanceColors.ToColor(MirrorAppearanceColors.DefaultBackgroundHex, MirrorAppearanceColors.DefaultBackgroundHex);

    /// <summary>0..1 opacity for the translated glyph halo/background.</summary>
    public double TranslationBackgroundOpacity { get; set; } = 0.72;

    /// <summary>Ink color for translated glyphs.</summary>
    public Color TranslationTextColor { get; set; } =
        MirrorAppearanceColors.ToColor(MirrorAppearanceColors.DefaultTextHex, MirrorAppearanceColors.DefaultTextHex);

    /// <summary>Manual size multiplier for translated text (live A−/A+).</summary>
    public double TextScale { get; set; } = 1.0;

    /// <summary>Line-height ratio for wrapped translated lines. User-adjustable from settings.</summary>
    public double LineSpacingScale { get; set; } = 1.08;

    /// <summary>Vertical scroll offset for translated overlay content that grows below the glass.</summary>
    public double ContentScrollY { get; set; }

    /// <summary>Maximum useful value for <see cref="ContentScrollY"/> computed from the latest draw.</summary>
    public double MaxContentScrollY { get; private set; }

    /// <summary>How translated text is sized/arranged over the original.</summary>
    public OverlayLayoutMode LayoutMode { get; set; } = OverlayLayoutMode.Readable;

    /// <summary>Readable-mode floor in canvas units, scaled by the window so it adapts to size.</summary>
    public float MinReadableDip { get; set; } = 13f;

    /// <summary>When false we are in live see-through mode: no dimming, no translated text.</summary>
    public bool ShowTranslations { get; set; }

    /// <summary>Right-to-left target language (Arabic/Persian/Urdu) → right-aligned overlay text.</summary>
    public bool RightToLeft { get; set; } = true;

    /// <summary>Translated/source block selected by right-click for dictionary inspection.</summary>
    public TranslatedBlock? SelectedBlock { get; set; }

    /// <summary>Selected word/term inside <see cref="SelectedBlock"/> for dictionary inspection.</summary>
    public string? SelectedText { get; set; }

    /// <summary>Actual rendered word/line rectangles in canvas coordinates, rebuilt every frame for hit testing.</summary>
    public IReadOnlyList<TextHitRegion> HitRegions => _hitRegions;

    /// <summary>While dragging/resizing, hide the frozen capture and text so the real desktop shows through.</summary>
    public bool ManipulatingGlass { get; set; }

    /// <summary>Faint tint shown when there is no capture yet, so the empty glass is visible.</summary>
    public bool ShowIdleHint { get; set; } = true;

    // ── Futuristic HUD effects ──────────────────────────────────────────────
    /// <summary>0..1 vertical position of the holographic scan beam (animated during processing).</summary>
    public float ScanProgress { get; set; }
    /// <summary>When true a scan beam sweeps the glass (capture/OCR/translate in progress).</summary>
    public bool Processing { get; set; }
    /// <summary>0..1 phase driving the pulsing neon glow of the HUD frame and blocks.</summary>
    public float GlowPhase { get; set; }
    /// <summary>Draw the neon corner brackets + edge frame that make the window read as a HUD lens.</summary>
    public bool ShowHud { get; set; } = true;

    /// <summary>When set, the next frame logs each block's computed draw geometry (diagnostics).</summary>
    public bool LogNextFrame { get; set; }

    private static readonly Color Neon = Color.FromRgb(0x22, 0xE5, 0xFF);   // electric cyan
    private static readonly Color Neon2 = Color.FromRgb(0xA8, 0x55, 0xF7);  // violet

    // Precomputed per-frame so all lines can share a size in Uniform mode.
    private float _uniformBoxH;
    private float _flowBottom;
    private DocumentLayoutProfile _layoutProfile = DocumentLayoutProfile.Default;
    private readonly List<TextHitRegion> _hitRegions = new();

    private sealed class DocumentLayoutProfile
    {
        public float BodyFontSize { get; init; }
        public float CaptionFontSize { get; init; }
        public float HeadingFontSize { get; init; }
        public float TitleFontSize { get; init; }
        public float LineSpacing { get; init; }
        public float ParagraphGap { get; init; }
        public float ContentLeft { get; init; }
        public float ContentRight { get; init; }
        public bool IsDocument { get; init; }

        public static DocumentLayoutProfile Default => new()
        {
            BodyFontSize = 16f,
            CaptionFontSize = 14f,
            HeadingFontSize = 19f,
            TitleFontSize = 22f,
            LineSpacing = 1.22f,
            ParagraphGap = 4f,
            ContentLeft = 12f,
            ContentRight = 520f,
            IsDocument = false,
        };
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        try { DrawCore(canvas, dirtyRect); }
        catch (Exception ex) { MirrorLog.Error("MirrorDrawable.Draw", ex); }
    }

    private void DrawCore(ICanvas canvas, RectF dirtyRect)
    {
        if (ManipulatingGlass)
        {
            DrawManipulationGlass(canvas, dirtyRect);
            return;
        }

        if (Background != null && CaptureWidth > 0 && CaptureHeight > 0)
        {
            canvas.DrawImage(Background, dirtyRect.X, dirtyRect.Y, dirtyRect.Width, dirtyRect.Height);
        }
        else if (ShowIdleHint)
        {
            DrawIdleGlass(canvas, dirtyRect);
        }

        if (ShowTranslations && Blocks.Count > 0 && CaptureWidth > 0 && CaptureHeight > 0)
            DrawTranslations(canvas, dirtyRect);

        if (Processing) DrawScanBeam(canvas, dirtyRect);
        if (ShowHud) DrawHudFrame(canvas, dirtyRect);
    }

    private void DrawManipulationGlass(ICanvas canvas, RectF r)
    {
        float pulse = 0.5f + 0.5f * MathF.Sin(GlowPhase * MathF.PI * 2);
        canvas.FillColor = Colors.Transparent;
        canvas.FillRectangle(r);

        canvas.StrokeSize = 1.5f;
        canvas.StrokeColor = Neon.WithAlpha(0.26f + 0.22f * pulse);
        canvas.DrawRoundedRectangle(new RectF(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8), 10);

        canvas.StrokeSize = 1f;
        canvas.StrokeColor = Neon2.WithAlpha(0.18f);
        canvas.DrawLine(r.X + 16, r.Y + 16, r.Right - 16, r.Y + 16);
        canvas.DrawLine(r.X + 16, r.Bottom - 16, r.Right - 16, r.Bottom - 16);
    }

    private void DrawTranslations(ICanvas canvas, RectF dirtyRect)
    {
        _hitRegions.Clear();
        bool readerPage = TranslationBackgroundOpacity >= 0.995;
        if (readerPage)
        {
            canvas.FillColor = TranslationBackgroundColor.WithAlpha(1f);
            canvas.FillRectangle(dirtyRect);
        }
        else if (Dim > 0)
        {
            // Slight global dimming so translated text reads clearly over the original.
            canvas.FillColor = new Color(0f, 0f, 0f, (float)Math.Clamp(Dim, 0, 1));
            canvas.FillRectangle(dirtyRect);
        }

        float sx = dirtyRect.Width / CaptureWidth;
        float sy = dirtyRect.Height / CaptureHeight;

        // Window-adaptive: scale the readable floor with the window so text grows/shrinks with it.
        float windowScale = MathF.Max(0.5f, MathF.Min(sx, sy));

        // Uniform mode: one shared size = median original line height (in canvas units).
        _uniformBoxH = 0f;
        if (LayoutMode == OverlayLayoutMode.Uniform && Blocks.Count > 0)
        {
            var hs = Blocks.Select(b => b.Height * sy).OrderBy(h => h).ToList();
            _uniformBoxH = hs[hs.Count / 2];
        }
        float scale = (float)Math.Clamp(TextScale, 0.75, 3.0);
        _layoutProfile = BuildDocumentLayoutProfile(dirtyRect, sx, sy, windowScale, scale);

        float scroll = (float)Math.Max(0, ContentScrollY);
        _flowBottom = dirtyRect.Y - scroll;
        foreach (var block in Blocks.OrderBy(b => b.Y).ThenBy(b => b.X))
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;
            try { DrawBlock(canvas, dirtyRect, block, sx, sy, windowScale, scroll); }
            catch (Exception ex) { MirrorLog.Error("DrawBlock", ex); }
        }
        MaxContentScrollY = Math.Max(0, _flowBottom + scroll - dirtyRect.Bottom + 8);
        if (ContentScrollY > MaxContentScrollY + 1)
            ContentScrollY = MaxContentScrollY;
        LogNextFrame = false;
    }

    private void DrawBlock(ICanvas canvas, RectF dirtyRect, TranslatedBlock block, float sx, float sy, float windowScale, float scroll)
    {
        float bx = dirtyRect.X + block.X * sx;
        float by = dirtyRect.Y + block.Y * sy - scroll;
        float bw = MathF.Max(8, block.Width * sx);
        float bh = MathF.Max(8, block.Height * sy);
        float lineBoxH = MathF.Max(8, (block.LineHeightHint > 0 ? block.LineHeightHint : block.Height) * sy);

        float scale = (float)Math.Clamp(TextScale, 0.75, 3.0);
        float fontSize = ResolveDocumentFontSize(block, lineBoxH, windowScale, scale);

        bool hasLtrRun = ContainsLtrRun(block.TranslatedText);
        float pad = Math.Clamp(fontSize * 0.18f, 3f, 8f);

        float maxW = MathF.Max(1, dirtyRect.Width - 12f);
        float roleWidthFactor = block.Font.Role switch
        {
            DocumentTextRole.Title => 1.70f,
            DocumentTextRole.Heading => 1.55f,
            DocumentTextRole.Caption => 1.15f,
            _ => hasLtrRun ? 1.42f : 1.32f,
        };
        float minW = MathF.Min(maxW, MathF.Max(bw, fontSize * 9f));
        float drawW = Math.Clamp(bw * roleWidthFactor, minW, maxW);
        float drawX;

        if (_layoutProfile.IsDocument)
        {
            var contentLeft = Math.Clamp(_layoutProfile.ContentLeft, dirtyRect.X + 8f, dirtyRect.Right - 24f);
            var contentRight = Math.Clamp(_layoutProfile.ContentRight, contentLeft + 24f, dirtyRect.Right - 8f);
            var contentWidth = MathF.Max(24f, contentRight - contentLeft);
            float widthFloor = block.Font.Role switch
            {
                DocumentTextRole.Title => contentWidth,
                DocumentTextRole.Heading => contentWidth * 0.92f,
                DocumentTextRole.Caption => contentWidth * 0.66f,
                _ => contentWidth * 0.86f,
            };
            if (TranslationBackgroundOpacity >= 0.995)
                widthFloor = contentWidth;
            drawW = Math.Clamp(MathF.Max(drawW, widthFloor), MathF.Min(minW, contentWidth), contentWidth);
            drawX = RightToLeft ? contentRight - drawW : contentLeft;
        }
        else
        {
            drawX = RightToLeft ? (bx + bw) - drawW : bx;
        }

        if (drawX < dirtyRect.X) drawX = dirtyRect.X;
        if (drawX + drawW > dirtyRect.Right) drawX = dirtyRect.Right - drawW;

        var textLines = WrapTextLines(block.TranslatedText, MathF.Max(1, drawW - pad * 2), fontSize, RightToLeft);
        float lineHeight = MathF.Max(fontSize * _layoutProfile.LineSpacing, fontSize * 1.16f);
        float drawH = MathF.Max(bh, lineHeight * textLines.Count + pad);
        float preferredY = textLines.Count == 1 ? by + MathF.Max(0, (bh - drawH) / 2f) : by;
        float blockGap = _layoutProfile.ParagraphGap;
        float drawY = MathF.Max(preferredY, _flowBottom + blockGap);
        _flowBottom = MathF.Max(_flowBottom, drawY + drawH);

        if (LogNextFrame)
            MirrorLog.Info($"  draw '{(block.TranslatedText.Length>20?block.TranslatedText.Substring(0,20):block.TranslatedText)}' box[{bx:0},{by:0},{bw:0}x{bh:0}] draw[{drawX:0},{drawY:0},{drawW:0}x{drawH:0}] fs={fontSize:0} canvas={dirtyRect.Width:0}x{dirtyRect.Height:0} sx={sx:0.00}");

        var sourceCore = new RectF(bx - 3, by - 2, bw + 6, bh + 4);
        RegisterSourceHitRegion(block, sourceCore);
        DrawSourceTextVeil(canvas, sourceCore, block.Font.Role);

        var family = FontMatcher.ResolveOverlayFamily(block.Font, RightToLeft, hasLtrRun);
        var mainFont = new Font(
            family,
            block.Font.Bold ? FontWeights.Bold : FontWeights.Regular,
            block.Font.Italic ? FontStyleType.Italic : FontStyleType.Normal);
        var latinFont = new Font(
            "Segoe UI",
            block.Font.Bold ? FontWeights.Bold : FontWeights.Regular,
            block.Font.Italic ? FontStyleType.Italic : FontStyleType.Normal);
        canvas.Font = mainFont;
        canvas.FontSize = fontSize;
        canvas.FontColor = TranslationTextColor.WithAlpha(0.98f);

        float textHeight = textLines.Count * lineHeight;
        float lineY = drawY + MathF.Max(pad * 0.35f, (drawH - textHeight) / 2f);
        var textCore = new RectF(drawX, drawY, drawW, drawH);
        _hitRegions.Add(new TextHitRegion(block, block.TranslatedText, textCore, IsWord: false));
        if (ReferenceEquals(block, SelectedBlock))
            DrawSelectionHighlight(canvas, sourceCore, textCore);

        foreach (var line in textLines)
        {
            var lineHits = RegisterLineHitRegions(block, line, drawX + pad, lineY, MathF.Max(1, drawW - pad * 2), lineHeight, fontSize, RightToLeft);
            DrawSelectedTermHighlight(canvas, block, lineHits);
            DrawOutputHalo(canvas, line, drawX + pad, lineY, MathF.Max(1, drawW - pad * 2), lineHeight,
                fontSize, mainFont);
            DrawOutputLine(canvas, line, drawX + pad, lineY, MathF.Max(1, drawW - pad * 2), lineHeight,
                fontSize, mainFont, latinFont);
            lineY += lineHeight;
        }
    }

    private void RegisterSourceHitRegion(TranslatedBlock block, RectF sourceCore)
    {
        var text = string.IsNullOrWhiteSpace(block.OriginalText) ? block.TranslatedText : block.OriginalText;
        if (string.IsNullOrWhiteSpace(text))
            return;

        _hitRegions.Add(new TextHitRegion(block, text.Trim(), sourceCore, IsWord: false));
    }

    private List<TextHitRegion> RegisterLineHitRegions(TranslatedBlock block, string line, float x, float y, float width, float height, float fontSize, bool rightToLeft)
    {
        var regions = new List<TextHitRegion>();
        if (string.IsNullOrWhiteSpace(line))
            return regions;

        var lineWidth = MathF.Min(width, EstimateSingleLineWidth(line, fontSize));
        var lineLeft = rightToLeft ? x + width - lineWidth : x;
        var lineRect = new RectF(lineLeft, y, lineWidth, height);
        AddHitRegion(regions, new TextHitRegion(block, line.Trim(), lineRect, IsWord: false));

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return regions;

        var space = MathF.Max(fontSize * 0.24f, fontSize * 0.35f);
        if (rightToLeft)
        {
            var cursor = lineRect.Right;
            foreach (var token in tokens)
            {
                var tokenWidth = MathF.Min(lineWidth, MathF.Max(fontSize * 0.8f, EstimateSingleLineWidth(token, fontSize)));
                var tokenRect = new RectF(cursor - tokenWidth, y, tokenWidth, height);
                AddHitRegion(regions, new TextHitRegion(block, CleanHitText(token), tokenRect, IsWord: true));
                cursor -= tokenWidth + space;
                if (cursor < lineRect.Left) break;
            }
        }
        else
        {
            var cursor = lineRect.Left;
            foreach (var token in tokens)
            {
                var tokenWidth = MathF.Min(lineWidth, MathF.Max(fontSize * 0.8f, EstimateSingleLineWidth(token, fontSize)));
                var tokenRect = new RectF(cursor, y, tokenWidth, height);
                AddHitRegion(regions, new TextHitRegion(block, CleanHitText(token), tokenRect, IsWord: true));
                cursor += tokenWidth + space;
                if (cursor > lineRect.Right) break;
            }
        }

        return regions;
    }

    private void AddHitRegion(List<TextHitRegion> lineRegions, TextHitRegion hit)
    {
        _hitRegions.Add(hit);
        lineRegions.Add(hit);
    }

    private void DrawSelectedTermHighlight(ICanvas canvas, TranslatedBlock block, IReadOnlyList<TextHitRegion> lineHits)
    {
        if (!ReferenceEquals(block, SelectedBlock) || string.IsNullOrWhiteSpace(SelectedText))
            return;

        var selected = CleanHitText(SelectedText);
        if (selected.Length == 0)
            return;

        foreach (var hit in lineHits)
        {
            if (!hit.IsWord || !string.Equals(CleanHitText(hit.Text), selected, StringComparison.OrdinalIgnoreCase))
                continue;

            canvas.FillColor = Color.FromRgba(255, 214, 10, 92);
            canvas.FillRoundedRectangle(hit.Bounds, 4);
            canvas.StrokeSize = 1.2f;
            canvas.StrokeColor = Color.FromRgba(255, 214, 10, 230);
            canvas.DrawRoundedRectangle(hit.Bounds, 4);
        }
    }

    private static string CleanHitText(string token)
        => token.Trim(' ', '.', ',', '،', ':', ';', '؛', '(', ')', '[', ']', '"', '\'');

    private void DrawOutputLine(ICanvas canvas, string line, float x, float y, float width, float height,
        float fontSize, Font mainFont, Font latinFont)
    {
        canvas.Font = mainFont;
        canvas.FontSize = fontSize;
        canvas.DrawString(
            PrepareDisplayText(line, RightToLeft),
            x, y, width, height,
            RightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment.Center,
            TextFlow.ClipBounds);
    }

    private void DrawOutputHalo(ICanvas canvas, string line, float x, float y, float width, float height,
        float fontSize, Font mainFont)
    {
        canvas.Font = mainFont;
        canvas.FontSize = fontSize;
        var haloOpacity = (float)Math.Clamp(TranslationBackgroundOpacity, 0, 1);
        if (haloOpacity <= 0.001f)
            return;

        canvas.FontColor = TranslationBackgroundColor.WithAlpha(haloOpacity * 0.58f);
        var display = PrepareDisplayText(line, RightToLeft);
        var alignment = RightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        foreach (var (dx, dy) in TextHaloOffsets)
            canvas.DrawString(display, x + dx, y + dy, width, height, alignment, VerticalAlignment.Center, TextFlow.ClipBounds);
        canvas.FontColor = TranslationTextColor.WithAlpha(0.98f);
    }

    private static string PrepareDisplayText(string text, bool rightToLeft)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Unicode bidi isolates keep LTR scientific tokens (CNS/LCNS/QKV/etc.) in their own
        // left-to-right run while the surrounding Arabic line remains one RTL paragraph.
        var isolated = LtrRunRegex.Replace(text, m => "\u202A" + m.Value + "\u202C");
        return rightToLeft ? "\u202B" + isolated + "\u202C" : isolated;
    }

    private static bool ContainsLtrRun(string text) => LtrRunRegex.IsMatch(text ?? string.Empty);

    private static IReadOnlyList<(string Text, bool IsLatin)> SplitDirectionRuns(string text)
    {
        var source = text ?? string.Empty;
        var runs = new List<(string Text, bool IsLatin)>();
        int pos = 0;
        foreach (Match match in LtrRunRegex.Matches(source))
        {
            AddRun(source.Substring(pos, match.Index - pos), false);
            AddRun(match.Value, true);
            pos = match.Index + match.Length;
        }
        AddRun(source.Substring(pos), false);
        return runs;

        void AddRun(string value, bool isLatin)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (runs.Count > 0 && runs[^1].IsLatin == isLatin)
                runs[^1] = (runs[^1].Text + value, isLatin);
            else
                runs.Add((value, isLatin));
        }
    }

    private DocumentLayoutProfile BuildDocumentLayoutProfile(RectF dirtyRect, float sx, float sy, float windowScale, float scale)
    {
        var allHeights = Blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.TranslatedText))
            .Select(b => (b.LineHeightHint > 0 ? b.LineHeightHint : b.Height) * sy)
            .Where(h => h >= 6f && h <= dirtyRect.Height * 0.45f)
            .OrderBy(h => h)
            .ToList();

        var bodyHeights = Blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.TranslatedText))
            .Where(b => b.Font.Role is DocumentTextRole.Body or DocumentTextRole.Caption)
            .Select(b => (b.LineHeightHint > 0 ? b.LineHeightHint : b.Height) * sy)
            .Where(h => h >= 6f && h <= dirtyRect.Height * 0.35f)
            .OrderBy(h => h)
            .ToList();

        var medianLineHeight = Median(bodyHeights.Count >= 3 ? bodyHeights : allHeights);
        if (medianLineHeight <= 0) medianLineHeight = 22f * MathF.Max(0.7f, windowScale);

        float viewportScale = Math.Clamp(dirtyRect.Height / 340f, 0.85f, 1.35f);
        float rawBody = medianLineHeight * 0.56f;
        float body = Math.Clamp(rawBody, 13.5f * viewportScale, 18.5f * viewportScale) * scale;
        body = Math.Clamp(body, 10.5f, 42f);

        float lineSpacing = (float)Math.Clamp(LineSpacingScale, 0.95, 1.55);
        if (RightToLeft) lineSpacing = MathF.Max(lineSpacing, 1.20f);

        var contentBlocks = Blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.TranslatedText))
            .Where(b => b.Font.Role is DocumentTextRole.Body or DocumentTextRole.Caption)
            .ToList();
        var profileBlocks = contentBlocks.Count >= 1
            ? contentBlocks
            : Blocks.Where(b => !string.IsNullOrWhiteSpace(b.TranslatedText)).ToList();

        bool isDocument = Blocks.Count >= 4 && contentBlocks.Count >= 1;
        float contentLeft = dirtyRect.X + 12f;
        float contentRight = dirtyRect.Right - 12f;
        if (profileBlocks.Count > 0)
        {
            contentLeft = MathF.Max(dirtyRect.X + 10f, profileBlocks.Min(b => dirtyRect.X + b.X * sx) - body * 0.55f);
            contentRight = MathF.Min(dirtyRect.Right - 10f, profileBlocks.Max(b => dirtyRect.X + (b.X + b.Width) * sx) + body * 0.55f);
            if (contentRight - contentLeft < dirtyRect.Width * 0.42f)
            {
                contentLeft = dirtyRect.X + dirtyRect.Width * 0.12f;
                contentRight = dirtyRect.Right - dirtyRect.Width * 0.08f;
            }
        }

        return new DocumentLayoutProfile
        {
            BodyFontSize = body,
            CaptionFontSize = body * 0.88f,
            HeadingFontSize = Math.Clamp(body * 1.18f, body + 1f, body * 1.35f),
            TitleFontSize = Math.Clamp(body * 1.38f, body + 3f, body * 1.65f),
            LineSpacing = lineSpacing,
            ParagraphGap = MathF.Max(2f, body * 0.16f),
            ContentLeft = contentLeft,
            ContentRight = contentRight,
            IsDocument = isDocument,
        };
    }

    private float ResolveDocumentFontSize(TranslatedBlock block, float boxHeight, float windowScale, float scale)
    {
        float roleSize = block.Font.Role switch
        {
            DocumentTextRole.Caption => _layoutProfile.CaptionFontSize,
            DocumentTextRole.Heading => _layoutProfile.HeadingFontSize,
            DocumentTextRole.Title => _layoutProfile.TitleFontSize,
            _ => _layoutProfile.BodyFontSize,
        };

        if (LayoutMode == OverlayLayoutMode.Uniform)
            return MathF.Max(_layoutProfile.BodyFontSize, MathF.Max(12f, MinReadableDip * windowScale * scale));

        // MatchOriginal still honours source hierarchy, but only within a narrow band around the
        // document profile. Raw OCR heights are noisy across languages and caused chaotic baselines.
        if (LayoutMode == OverlayLayoutMode.MatchOriginal)
        {
            float sourceSize = boxHeight * 0.56f * scale;
            return Math.Clamp(sourceSize, roleSize * 0.86f, roleSize * 1.18f);
        }

        return MathF.Max(roleSize, MathF.Max(12f, MinReadableDip * windowScale * scale));
    }

    private static float Median(IReadOnlyList<float> values)
    {
        if (values.Count == 0) return 0;
        return values[values.Count / 2];
    }

    private void DrawSourceTextVeil(ICanvas canvas, RectF sourceCore, DocumentTextRole role)
    {
        if (TranslationBackgroundOpacity >= 0.995)
            return;

        if (role is DocumentTextRole.Body or DocumentTextRole.Caption)
            return;

        float alpha = (float)Math.Clamp(TranslationBackgroundOpacity, 0, 1) *
            (role is DocumentTextRole.Title ? 0.18f : 0.12f);
        if (alpha <= 0.001f)
            return;

        canvas.FillColor = TranslationBackgroundColor.WithAlpha(alpha);
        canvas.FillRoundedRectangle(sourceCore, 2);
    }

    private void DrawSelectionHighlight(ICanvas canvas, RectF sourceCore, RectF textCore)
    {
        canvas.FillColor = Color.FromRgba(255, 214, 10, 54);
        canvas.FillRoundedRectangle(sourceCore, 3);
        canvas.StrokeSize = 1.5f;
        canvas.StrokeColor = Color.FromRgba(255, 214, 10, 210);
        canvas.DrawRoundedRectangle(textCore, 4);
    }

    private static IReadOnlyList<string> WrapTextLines(string text, float maxWidth, float fontSize, bool rightToLeft)
    {
        var normalized = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");
        if (normalized.Length == 0) return Array.Empty<string>();
        if (maxWidth <= fontSize * 5f) return new[] { normalized };

        var lines = new List<string>();
        string current = "";
        foreach (var token in BuildWrapUnits(normalized, rightToLeft))
        {
            string candidate = current.Length == 0 ? token : current + " " + token;
            if (current.Length > 0 && EstimateSingleLineWidth(candidate, fontSize) > maxWidth)
            {
                lines.Add(current);
                current = token;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines.Count == 0 ? new[] { normalized } : lines;
    }

    private static IReadOnlyList<string> BuildWrapUnits(string normalized, bool rightToLeft)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!rightToLeft || tokens.Length <= 1)
            return tokens;

        var units = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (ShouldBindToPreviousRtlWord(token) && units.Count > 0 && ContainsRtlLetter(units[^1]))
                units[^1] = units[^1] + " " + token;
            else
                units.Add(token);
        }

        return units;
    }

    private static bool ShouldBindToPreviousRtlWord(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || ContainsRtlLetter(token))
            return false;

        int latinLetters = token.Count(ch => ch <= 0x7F && char.IsLetter(ch));
        if (latinLetters == 0)
            return false;

        int visibleChars = token.Count(ch => !char.IsWhiteSpace(ch));
        return visibleChars <= 24;
    }

    private static bool ContainsRtlLetter(string text)
        => !string.IsNullOrEmpty(text) && text.Any(ch => ch is >= '\u0590' and <= '\u08FF');

    private static float EstimateSingleLineWidth(string text, float fontSize)
    {
        if (string.IsNullOrWhiteSpace(text)) return fontSize;

        float units = 0;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch)) units += 0.35f;
            else if (ch <= 0x7F && (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.')) units += 0.58f;
            else if (ch >= 0x0600 && ch <= 0x06FF) units += 0.68f;
            else units += 0.62f;
        }
        return MathF.Max(fontSize, units * fontSize);
    }

    private static float EstimateInlineRunWidth(string text, float fontSize, bool latin)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        float units = 0;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch)) units += 0.22f;
            else if (ch <= 0x7F && (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.')) units += latin ? 0.50f : 0.46f;
            else if (ch >= 0x0600 && ch <= 0x06FF) units += 0.52f;
            else units += 0.34f;
        }

        return MathF.Max(1f, units * fontSize + 1.5f);
    }

    private static readonly Regex LtrRunRegex = new(
        @"(?<![\p{L}\p{N}])([A-Za-z][A-Za-z0-9+.#]*(?:[-_/][A-Za-z0-9+.#]+)*)(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (float X, float Y)[] TextHaloOffsets =
    {
        (-0.8f, 0f), (0.8f, 0f), (0f, -0.8f), (0f, 0.8f),
        (-0.55f, -0.55f), (0.55f, -0.55f), (-0.55f, 0.55f), (0.55f, 0.55f),
    };

    // ── Holographic glass when idle (no capture yet) ────────────────────────
    private void DrawIdleGlass(ICanvas canvas, RectF r)
    {
        canvas.FillColor = new Color(0.03f, 0.05f, 0.10f, 0.32f);
        canvas.FillRectangle(r);

        // faint scan grid
        canvas.StrokeColor = Neon.WithAlpha(0.06f);
        canvas.StrokeSize = 1;
        for (float gy = r.Y + 14; gy < r.Bottom; gy += 28)
            canvas.DrawLine(r.X, gy, r.Right, gy);

        float pulse = 0.5f + 0.5f * MathF.Sin(GlowPhase * MathF.PI * 2);
        canvas.FontColor = Neon.WithAlpha(0.55f + 0.35f * pulse);
        canvas.FontSize = 15;
        canvas.Font = Font.DefaultBold;
        canvas.DrawString("◆  المرآة جاهزة · ضع فوق نص ثم اضغط ترجم",
            r.X, r.Y, r.Width, r.Height, HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    // ── Animated holographic scan beam during processing ────────────────────
    private void DrawScanBeam(ICanvas canvas, RectF r)
    {
        float y = r.Y + r.Height * Math.Clamp(ScanProgress, 0f, 1f);
        float band = MathF.Max(14f, r.Height * 0.06f);
        var grad = new LinearGradientPaint(
            new PaintGradientStop[]
            {
                new(0f, Neon.WithAlpha(0f)),
                new(0.5f, Neon.WithAlpha(0.32f)),
                new(1f, Neon.WithAlpha(0f)),
            },
            new PointF(0, (y - band - r.Y) / MathF.Max(1, r.Height)),
            new PointF(0, (y + band - r.Y) / MathF.Max(1, r.Height)));
        canvas.SetFillPaint(grad, new RectF(r.X, y - band, r.Width, band * 2));
        canvas.FillRectangle(r.X, y - band, r.Width, band * 2);

        canvas.StrokeColor = Neon.WithAlpha(0.9f);
        canvas.StrokeSize = 1.5f;
        canvas.DrawLine(r.X, y, r.Right, y);
    }

    // ── Neon HUD corner brackets + edge frame (the "lens") ──────────────────
    private void DrawHudFrame(ICanvas canvas, RectF r)
    {
        float pulse = 0.6f + 0.4f * MathF.Sin(GlowPhase * MathF.PI * 2);
        float len = MathF.Min(26f, MathF.Min(r.Width, r.Height) * 0.16f);
        float inset = 3f;
        float x0 = r.X + inset, y0 = r.Y + inset, x1 = r.Right - inset, y1 = r.Bottom - inset;

        // subtle full frame
        canvas.StrokeSize = 1f;
        canvas.StrokeColor = Neon.WithAlpha(0.14f);
        canvas.DrawRoundedRectangle(x0, y0, x1 - x0, y1 - y0, 8);

        canvas.StrokeSize = 2.4f;
        canvas.StrokeColor = Neon.WithAlpha(0.5f + 0.45f * pulse);
        // TL
        canvas.DrawLine(x0, y0 + len, x0, y0); canvas.DrawLine(x0, y0, x0 + len, y0);
        // TR
        canvas.DrawLine(x1 - len, y0, x1, y0); canvas.DrawLine(x1, y0, x1, y0 + len);
        // BL
        canvas.DrawLine(x0, y1 - len, x0, y1); canvas.DrawLine(x0, y1, x0 + len, y1);
        // BR
        canvas.DrawLine(x1 - len, y1, x1, y1); canvas.DrawLine(x1, y1, x1, y1 - len);
    }
}
