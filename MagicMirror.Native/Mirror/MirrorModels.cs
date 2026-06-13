namespace MagicMirror.Native.Mirror;

/// <summary>
/// A rectangular region of pixels captured from the screen behind the mirror.
/// Pixels are 32-bit BGRA (premultiplied = false), row-major, length = Width*Height*4.
/// </summary>
public sealed class CaptureResult
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>PNG-encoded copy of the capture (used for OCR file hand-off and overlay display).</summary>
    public byte[]? Png { get; init; }

    /// <summary>Screen-space origin of the captured region (device pixels).</summary>
    public int ScreenX { get; init; }
    public int ScreenY { get; init; }

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// One recognised text region from OCR. Coordinates are in pixels relative to the
/// captured image (top-left origin), matching <see cref="CaptureResult"/>.
/// </summary>
public sealed class OcrTextRegion
{
    public required string Text { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>
    /// Original single-line height when this region represents a merged paragraph. Font detection
    /// should use this instead of the full paragraph box height.
    /// </summary>
    public int LineHeightHint { get; init; }

    /// <summary>OCR confidence in the range 0..100 (negative when unknown).</summary>
    public float Confidence { get; init; } = -1f;

    /// <summary>BCP-47 / ISO code of the detected source language when the engine reports it.</summary>
    public string? SourceLanguage { get; init; }
}

/// <summary>
/// Detected typographic attributes for an OCR region, used to render the translated
/// overlay so it visually matches the original text. Ported from the THEORYS
/// <c>services/ocr/font_matcher.py</c> heuristics.
/// </summary>
public sealed class FontInfo
{
    public string Family { get; init; } = "Segoe UI";
    public double SizePt { get; init; } = 11;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Serif { get; init; } = true;
    public DocumentTextRole Role { get; init; } = DocumentTextRole.Body;
    public TypographyEra Era { get; init; } = TypographyEra.FormalAcademic;
}

/// <summary>Semantic typographic role inferred from a source text box.</summary>
public enum DocumentTextRole
{
    Caption,
    Body,
    Heading,
    Title,
}

/// <summary>Coarse historical/style family inferred from document typography and vocabulary.</summary>
public enum TypographyEra
{
    Unknown,
    ClassicalPrint,
    FormalAcademic,
    ModernDigital,
    Newspaper,
}

/// <summary>
/// A fully-resolved overlay element: the original recognised text, its translation,
/// the box it occupies (image pixel space), and the font to render it with.
/// </summary>
public sealed class TranslatedBlock
{
    public required string OriginalText { get; set; }
    public string TranslatedText { get; set; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int LineHeightHint { get; init; }
    public FontInfo Font { get; init; } = new();
    public float Confidence { get; init; } = -1f;
}

/// <summary>Which OCR engine the pipeline should prefer.</summary>
public enum OcrEnginePreference
{
    /// <summary>Tesseract (THEORYS eng+ara) first, OS OCR as fallback.</summary>
    TesseractThenNative,
    /// <summary>OS OCR (Windows.Media.Ocr) only.</summary>
    NativeOnly,
    /// <summary>Tesseract only (no fallback).</summary>
    TesseractOnly,
}

/// <summary>How translated text is sized/arranged over the original.</summary>
public enum OverlayLayoutMode
{
    /// <summary>Each line fills its original box — preserves the source's relative sizes.</summary>
    MatchOriginal,
    /// <summary>Like MatchOriginal but never below a readable floor (small text is enlarged).</summary>
    Readable,
    /// <summary>All lines share one size (median) for a clean, uniform look.</summary>
    Uniform,
}

/// <summary>
/// User-configurable behaviour of the mirror. Persisted as JSON in app data so the
/// web layer (settings UI) and the native overlay share one source of truth.
/// </summary>
public sealed class MirrorSettings
{
    /// <summary>BCP-47 code of the language the mirror translates INTO (the "main language"). Default Arabic.</summary>
    public string TargetLanguage { get; set; } = "ar";

    /// <summary>Hint of the source/original language for OCR ("auto" lets the engine decide). e.g. "en".</summary>
    public string SourceLanguageHint { get; set; } = "auto";

    /// <summary>Tesseract language string, e.g. "eng+ara".</summary>
    public string TesseractLanguages { get; set; } = "eng+ara";

    /// <summary>0..1 opacity of the dimming layer drawn over the original text so the translation reads clearly.</summary>
    public double DimAmount { get; set; } = 0.45;

    /// <summary>Hex RGB color used for the translated text background/halo, e.g. #F3F6EF.</summary>
    public string TranslationBackgroundColor { get; set; } = MirrorAppearanceColors.DefaultBackgroundHex;

    /// <summary>0..1 opacity of the translated text background/halo.</summary>
    public double TranslationBackgroundOpacity { get; set; } = 0.72;

    /// <summary>Hex RGB color used for translated text glyphs, e.g. #0D0F14.</summary>
    public string TranslationTextColor { get; set; } = MirrorAppearanceColors.DefaultTextHex;

    /// <summary>Manual multiplier for translated-text size (0.75..3.0). Adjustable live from the overlay (A−/A+).</summary>
    public double TextScale { get; set; } = 1.0;

    /// <summary>Line-height ratio for wrapped translated lines. Arabic defaults compact enough for overlay reading.</summary>
    public double LineSpacingScale { get; set; } = 1.08;

    /// <summary>How translated text is sized/arranged over the original.</summary>
    public OverlayLayoutMode LayoutMode { get; set; } = OverlayLayoutMode.Readable;

    /// <summary>Live see-through preview frames per second while idle (0 disables the live mirror).</summary>
    public double IdlePreviewFps { get; set; } = 4;

    public OcrEnginePreference OcrEngine { get; set; } = OcrEnginePreference.TesseractThenNative;

    /// <summary>
    /// Prefer reading text straight from the window (UI Automation) instead of OCR when the app
    /// exposes it — faster and lighter. Falls back to OCR automatically when unavailable.
    /// </summary>
    public bool UseWindowText { get; set; } = true;

    /// <summary>
    /// Primary AI gateway base URL (the app's dedicated Cloudflare Worker).
    /// The mirror POSTs to "{GatewayBaseUrl}/api/sarmad/ask".
    /// </summary>
    public string GatewayBaseUrl { get; set; } = "https://magicmirror-sarmad-gateway.2sa.workers.dev";

    /// <summary>
    /// Optional fallback Sarmad mesh used when the primary gateway is empty/unreachable.
    /// Empty by default because the documentation gateway is not a product endpoint.
    /// </summary>
    public string FallbackSarmadUrl { get; set; } = "";

    /// <summary>Cloudflare Workers AI model id requested through the mesh.</summary>
    public string AiModel { get; set; } = "@cf/openai/gpt-oss-20b";

    /// <summary>Optional explicit path to a Windows tesseract.exe; empty = auto-discover.</summary>
    public string TesseractExePath { get; set; } = "";

    /// <summary>tessdata directory (THEORYS eng+ara/osd traineddata).</summary>
    public string TessDataPath { get; set; } =
        @"X:\source\THEORYS\Items\tesseract-complete-package\models\tesseract-ocr\5\tessdata";
}
