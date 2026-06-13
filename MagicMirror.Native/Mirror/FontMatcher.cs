namespace MagicMirror.Native.Mirror;

/// <summary>
/// Detects approximate typographic attributes (point size, serif/sans, weight, style) for an
/// OCR text region so the translated overlay can be rendered to visually match the original.
///
/// This is a faithful C# port of the THEORYS reference implementation
/// <c>X:\source\THEORYS\services\ocr\font_matcher.py</c> (class <c>FontMatcher</c>), adapted
/// from "OCR → LaTeX" to "OCR → on-screen overlay". The numeric model is preserved so results
/// reproduce the same quality:
///
///   • Point-size estimate:  <c>sizePt = bboxHeightPx / 1.33</c>
///     (1 pt ≈ 1.333 px at 96 DPI; equivalently sizePt ≈ 0.75·height, i.e. cap-height fit.)
///   • Serif default:        true (academic/body text bias, as in the Python source).
///   • Bold heuristic:       SHORT all-caps strings (≤5 words) OR pure numeric/currency tokens.
///   • Italic heuristic:     false (not reliably inferable from a bounding box alone).
///   • Role/style profile:   title/heading/body/caption + coarse era (academic, classical print,
///     modern UI, newspaper) so the target language can use a culturally equivalent font instead of
///     a single generic fallback.
///
/// To reuse and reproduce identical detection elsewhere, feed the same inputs
/// (bounding-box height in pixels at the capture DPI, and the recognised text) — the
/// thresholds and constants below are the full protocol.
/// </summary>
public static class FontMatcher
{
    /// <summary>Pixels-per-point conversion at 96 DPI (THEORYS constant: height/1.33).</summary>
    public const double PixelsPerPoint = 1.33;

    /// <summary>Lines taller than this many points are treated as headings (sans + bold).</summary>
    private const double HeadingPointThreshold = 15.0;

    /// <summary>
    /// Detects a <see cref="FontInfo"/> from an OCR region's bounding-box height and text.
    /// Direct port of <c>FontMatcher.detect_font_from_bbox</c> plus the heading rule from
    /// <c>analyze_document_fonts</c>.
    /// </summary>
    /// <param name="bboxHeightPx">Region height in pixels (at the capture resolution).</param>
    /// <param name="text">Recognised text (used for the bold/heading heuristics).</param>
    /// <param name="targetLanguage">Overlay language; selects an appropriate rendering family.</param>
    public static FontInfo Detect(int bboxHeightPx, string text, string targetLanguage)
    {
        // sizePt = bbox_height / 1.33   (THEORYS px→pt)
        double sizePt = bboxHeightPx / PixelsPerPoint;
        if (sizePt < 6) sizePt = 6;          // floor — unreadable below ~6pt
        if (sizePt > 96) sizePt = 96;        // sane ceiling

        var role = DetectRole(sizePt, text);
        var era = DetectEra(text, role);
        bool isBold = DetectBold(text) || role is DocumentTextRole.Title or DocumentTextRole.Heading;
        bool isSerif = !LooksLikeUiText(text); // scientific/book text stays serif unless it looks like UI chrome

        return new FontInfo
        {
            Family = ResolveFamily(isSerif, targetLanguage, role, era),
            SizePt = Math.Round(sizePt, 1),
            Bold = isBold,
            Italic = false,
            Serif = isSerif,
            Role = role,
            Era = era,
        };
    }

    /// <summary>
    /// Bold heuristic — port of <c>FontMatcher._detect_bold_from_text</c>:
    /// short ALL-CAPS strings (≤5 words) and standalone numeric/currency tokens.
    /// </summary>
    private static bool DetectBold(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        // Headings often UPPERCASE with few words.
        bool hasUpper = trimmed.Any(char.IsUpper);
        bool hasLower = trimmed.Any(char.IsLower);
        if (hasUpper && !hasLower && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5)
            return true;

        // Numeric/currency tokens in tables are usually bold:  ^\$?[\d,]+\.?\d*$
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\$?[\d,]+\.?\d*$");
    }

    /// <summary>
    /// Maps serif/sans intent to a concrete font family that can render the target language.
    /// Arabic overlays prefer Arabic-capable families; Latin overlays use the Python source's
    /// Times/Arial split.
    /// </summary>
    private static string ResolveFamily(bool serif, string targetLanguage, DocumentTextRole role, TypographyEra era)
    {
        bool arabic = (targetLanguage ?? "").StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        if (arabic)
        {
            if (!serif) return "Segoe UI";
            if (role == DocumentTextRole.Title || era == TypographyEra.ClassicalPrint)
                return "Arabic Typesetting";
            return "Traditional Arabic";
        }
        return serif ? "Times New Roman" : "Arial";
    }

    /// <summary>
    /// Chooses the actual overlay family after the line text is known. Mixed Arabic/Latin output
    /// keeps the source's serif/sans/cultural role instead of forcing every mixed line to one
    /// generic UI font.
    /// </summary>
    public static string ResolveOverlayFamily(FontInfo source, bool rightToLeft, bool hasLtrRun)
    {
        if (!rightToLeft) return source.Family;
        if (!hasLtrRun) return source.Family;

        if (!source.Serif) return "Segoe UI";
        if (source.Role == DocumentTextRole.Title || source.Era == TypographyEra.ClassicalPrint)
            return "Arabic Typesetting";
        return "Traditional Arabic";
    }

    private static DocumentTextRole DetectRole(double sizePt, string text)
    {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (sizePt >= 24 || (sizePt >= HeadingPointThreshold && words <= 8))
            return DocumentTextRole.Title;
        if (sizePt >= HeadingPointThreshold)
            return DocumentTextRole.Heading;
        if (sizePt <= 8.5)
            return DocumentTextRole.Caption;
        return DocumentTextRole.Body;
    }

    private static TypographyEra DetectEra(string text, DocumentTextRole role)
    {
        var s = (text ?? "").ToLowerInvariant();
        if (LooksLikeUiText(s)) return TypographyEra.ModernDigital;
        if (s.Contains("abstract") || s.Contains("theory") || s.Contains("framework") ||
            s.Contains("operator") || s.Contains("model") || s.Contains("version") ||
            s.Contains("doi") || s.Contains("draft"))
            return TypographyEra.FormalAcademic;
        if (s.Contains("press") || s.Contains("editorial") || s.Contains("newspaper") || s.Contains("journal"))
            return TypographyEra.Newspaper;
        if (role is DocumentTextRole.Body or DocumentTextRole.Title)
            return TypographyEra.ClassicalPrint;
        return TypographyEra.Unknown;
    }

    private static bool LooksLikeUiText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToLowerInvariant();
        return s.Contains("close") || s.Contains("maximize") || s.Contains("minimize") ||
               s.Contains("settings") || s.Contains("save") || s.Contains("cancel") ||
               s.Contains("button") || s.Contains("menu") || s.Contains("toolbar") ||
               s.Contains("ترجم") || s.Contains("إعدادات");
    }
}
