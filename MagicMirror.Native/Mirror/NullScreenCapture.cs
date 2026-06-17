namespace MagicMirror.Native.Mirror;

/// <summary>No-op text provider (used where UI Automation is unavailable); forces the OCR path.</summary>
public sealed class NullWindowTextProvider : IWindowTextProvider
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<OcrTextRegion>> ExtractTextAsync(
        int screenX, int screenY, int width, int height, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OcrTextRegion>>(Array.Empty<OcrTextRegion>());
}

/// <summary>No-op capture used on platforms whose native capture is not yet wired (keeps DI satisfied).</summary>
public sealed class NullScreenCapture : IScreenCapture
{
    public bool IsAvailable => false;

    public Task<CaptureResult> CaptureRegionAsync(int screenX, int screenY, int width, int height, double qualityScale = 1.0)
        => Task.FromResult(new CaptureResult { Pixels = Array.Empty<byte>(), Width = 0, Height = 0 });

    public Task<CaptureResult> CapturePreviewAsync(int screenX, int screenY, int width, int height, int maxDimension = 900)
        => Task.FromResult(new CaptureResult { Pixels = Array.Empty<byte>(), Width = 0, Height = 0 });
}
