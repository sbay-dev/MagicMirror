using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using MagicMirror.Native.Mirror;

namespace MagicMirror.Native.Platforms.Windows;

/// <summary>
/// Windows screen capture via GDI <c>BitBlt</c>. The mirror overlay window excludes itself
/// from the result through <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>
/// (see <see cref="MirrorWindowInterop"/>), so a straight desktop BitBlt returns exactly the
/// content behind the glass. Produces both raw BGRA pixels (for OS OCR) and a PNG (for
/// Tesseract hand-off and overlay display), encoded with the WinRT imaging APIs so no extra
/// native package is required.
/// </summary>
public sealed class WindowsScreenCapture : IScreenCapture
{
    public bool IsAvailable => true;

    public async Task<CaptureResult> CaptureRegionAsync(int screenX, int screenY, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return new CaptureResult { Pixels = Array.Empty<byte>(), Width = 0, Height = 0 };

        byte[] bgra = CaptureBgra(screenX, screenY, width, height);
        byte[]? png = await EncodeAsync(bgra, width, height, BitmapEncoder.PngEncoderId);

        return new CaptureResult
        {
            Pixels = bgra,
            Width = width,
            Height = height,
            Png = png,
            ScreenX = screenX,
            ScreenY = screenY,
        };
    }

    public async Task<CaptureResult> CapturePreviewAsync(int screenX, int screenY, int width, int height, int maxDimension = 900)
    {
        if (width <= 0 || height <= 0)
            return new CaptureResult { Pixels = Array.Empty<byte>(), Width = 0, Height = 0 };

        // Downscale so the live preview is cheap to encode/decode/draw (keeps dragging smooth).
        double scale = 1.0;
        int longest = Math.Max(width, height);
        if (longest > maxDimension) scale = (double)maxDimension / longest;
        int dw = Math.Max(1, (int)Math.Round(width * scale));
        int dh = Math.Max(1, (int)Math.Round(height * scale));

        byte[] bgra = CaptureBgraScaled(screenX, screenY, width, height, dw, dh);
        // BMP encode is far cheaper than PNG (no deflate) — fine for a transient preview frame.
        byte[]? img = await EncodeAsync(bgra, dw, dh, BitmapEncoder.BmpEncoderId);

        return new CaptureResult { Pixels = bgra, Width = dw, Height = dh, Png = img, ScreenX = screenX, ScreenY = screenY };
    }

    /// <summary>BitBlt the desktop into a 32-bit top-down DIB and return forced-opaque BGRA bytes.</summary>
    private static byte[] CaptureBgra(int x, int y, int w, int h)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr oldBmp = SelectObject(memDc, bmp);

        // CAPTUREBLT so layered windows blend correctly; excluded overlay is already filtered out.
        BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT);

        var bmi = new BITMAPINFO
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative = top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
            biColors = new uint[256],
        };

        byte[] buffer = new byte[w * h * 4];
        GetDIBits(memDc, bmp, 0, (uint)h, buffer, ref bmi, 0);

        // BitBlt leaves the alpha channel undefined — force fully opaque.
        for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;

        SelectObject(memDc, oldBmp);
        DeleteObject(bmp);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        return buffer;
    }

    /// <summary>BitBlt + StretchBlt the desktop into a smaller top-down DIB (downscaled preview).</summary>
    private static byte[] CaptureBgraScaled(int x, int y, int w, int h, int dw, int dh)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, dw, dh);
        IntPtr oldBmp = SelectObject(memDc, bmp);

        SetStretchBltMode(memDc, HALFTONE);
        StretchBlt(memDc, 0, 0, dw, dh, screenDc, x, y, w, h, SRCCOPY | CAPTUREBLT);

        var bmi = new BITMAPINFO
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = dw,
            biHeight = -dh,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
            biColors = new uint[256],
        };
        byte[] buffer = new byte[dw * dh * 4];
        GetDIBits(memDc, bmp, 0, (uint)dh, buffer, ref bmi, 0);
        for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;

        SelectObject(memDc, oldBmp);
        DeleteObject(bmp);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        return buffer;
    }

    private static async Task<byte[]?> EncodeAsync(byte[] bgra, int w, int h, Guid encoderId)
    {
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(encoderId, ms);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                (uint)w, (uint)h, 96, 96, bgra);
            await encoder.FlushAsync();

            var bytes = new byte[ms.Size];
            using var reader = new DataReader(ms.GetInputStreamAt(0));
            await reader.LoadAsync((uint)ms.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    // ── Win32 ───────────────────────────────────────────────────────────────
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int HALFTONE = 4;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int cxSrc, int cySrc, int rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] biColors;
    }
}
