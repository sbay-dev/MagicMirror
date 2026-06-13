namespace MagicMirror.Native.Mirror;

/// <summary>Captures the pixels of a screen region (used to grab what is behind the mirror glass).</summary>
public interface IScreenCapture
{
    /// <summary>
    /// Captures a screen rectangle in device pixels (full resolution, PNG-encoded for OCR).
    /// Implementations must exclude the mirror overlay itself from the result.
    /// </summary>
    Task<CaptureResult> CaptureRegionAsync(int screenX, int screenY, int width, int height);

    /// <summary>
    /// Fast, lower-resolution capture for the live see-through preview — downscaled and cheaply
    /// encoded so dragging stays smooth and CPU stays low. Not used for OCR.
    /// </summary>
    Task<CaptureResult> CapturePreviewAsync(int screenX, int screenY, int width, int height, int maxDimension = 900);

    /// <summary>True when the platform implementation is usable on this device.</summary>
    bool IsAvailable { get; }
}

/// <summary>Recognises text and bounding boxes from a captured image.</summary>
public interface IOcrEngine
{
    string Name { get; }

    /// <summary>Whether the engine can run right now (binaries/language data present).</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Runs OCR over a capture and returns word/line regions with bounding boxes
    /// relative to the captured image.
    /// </summary>
    Task<IReadOnlyList<OcrTextRegion>> RecognizeAsync(CaptureResult capture, MirrorSettings settings, CancellationToken ct = default);
}

/// <summary>Translates recognised text into the user's main language via the configured Sarmad mesh.</summary>
public interface ITranslationService
{
    /// <summary>
    /// Translates a batch of source strings into <paramref name="targetLanguage"/>.
    /// Returns translations aligned 1:1 with <paramref name="sources"/>; on failure the
    /// corresponding entry falls back to the original text.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> sources, string targetLanguage, MirrorSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Produces a dictionary-grade explanation for a selected word/phrase using full document context.
    /// The answer should include at least five translation alternatives and a decisive final summary.
    /// </summary>
    Task<string> ExplainDictionaryAsync(
        string selectedText, string documentContext, string targetLanguage, MirrorSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Extracts text and bounding boxes directly from the window under a screen rectangle (e.g. via
/// Windows UI Automation), so OCR can be skipped when the application already exposes its text.
/// </summary>
public interface IWindowTextProvider
{
    /// <summary>True when this provider can run on the current platform.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns text regions in capture-relative pixel coordinates (origin = the rectangle's
    /// top-left), or an empty list when the window exposes no usable text — in which case the
    /// caller falls back to OCR.
    /// </summary>
    Task<IReadOnlyList<OcrTextRegion>> ExtractTextAsync(
        int screenX, int screenY, int width, int height, CancellationToken ct = default);
}
