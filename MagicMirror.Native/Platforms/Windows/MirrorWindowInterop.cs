using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace MagicMirror.Native.Platforms.Windows;

/// <summary>
/// Win32/WinUI helpers that turn a MAUI window into the "magic mirror" glass: borderless,
/// always-on-top, resizable, and — crucially — excluded from screen capture via
/// <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> so the window never captures itself
/// (no infinite-mirror feedback) and the translated overlay it paints is never re-OCR'd.
/// </summary>
public static class MirrorWindowInterop
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int RGN_OR = 2;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int iMode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public static IntPtr GetHwnd(Microsoft.Maui.Controls.Window mauiWindow)
    {
        if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            return WindowNative.GetWindowHandle(native);
        return IntPtr.Zero;
    }

    public static AppWindow? GetAppWindow(Microsoft.Maui.Controls.Window mauiWindow)
    {
        var hwnd = GetHwnd(mauiWindow);
        if (hwnd == IntPtr.Zero) return null;
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    /// <summary>Applies the glass window styling (borderless, topmost). Capture-exclusion is applied
    /// only momentarily during our own BitBlt (see <see cref="SetExcludedFromCapture"/>), so the
    /// translated overlay stays visible to normal screenshots / screen sharing.</summary>
    public static void ConfigureOverlay(Microsoft.Maui.Controls.Window mauiWindow)
    {
        try
        {
            var hwnd = GetHwnd(mauiWindow);
            if (hwnd != IntPtr.Zero)
                SetWindowDisplayAffinity(hwnd, WDA_NONE); // capturable by default; excluded only per-capture

            var appWindow = GetAppWindow(mauiWindow);
            if (appWindow?.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = true;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
        }
        catch { /* handler not ready yet — caller retries on Created */ }
    }

    /// <summary>Toggle capture exclusion (off while we ourselves BitBlt, on otherwise).</summary>
    public static void SetExcludedFromCapture(Microsoft.Maui.Controls.Window mauiWindow, bool excluded)
    {
        var hwnd = GetHwnd(mauiWindow);
        if (hwnd != IntPtr.Zero)
            SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    /// <summary>Current window rectangle in physical (device) pixels — the region to capture/OCR.</summary>
    public static (int X, int Y, int Width, int Height) GetScreenRect(Microsoft.Maui.Controls.Window mauiWindow)
    {
        var appWindow = GetAppWindow(mauiWindow);
        if (appWindow == null) return (0, 0, 0, 0);
        var p = appWindow.Position;
        var s = appWindow.Size;
        return (p.X, p.Y, s.Width, s.Height);
    }

    public static void MoveResize(Microsoft.Maui.Controls.Window mauiWindow, int x, int y, int w, int h)
    {
        var appWindow = GetAppWindow(mauiWindow);
        appWindow?.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, w, h));
    }

    /// <summary>
    /// Clips the overlay HWND while dragging/resizing so only the toolbar and resize grip are part of
    /// the window shape. The mirror body is temporarily outside the HWND region, so the real desktop
    /// shows through without relying on a visible color key or fading the toolbar.
    /// </summary>
    public static void SetManipulationClip(Microsoft.Maui.Controls.Window mauiWindow, bool manipulating, double displayDensity)
    {
        var hwnd = GetHwnd(mauiWindow);
        if (hwnd == IntPtr.Zero) return;

        if (!manipulating)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (IsContentChildWindow(child))
                    SetWindowRgn(child, IntPtr.Zero, true);
                return true;
            }, IntPtr.Zero);
            return;
        }

        if (!GetWindowRect(hwnd, out var windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0)
            return;

        var density = displayDensity <= 0 ? 1.0 : displayDensity;
        var toolbarHeight = Math.Min(windowRect.Height, Math.Max(1, (int)Math.Ceiling(118 * density)));
        var gripSize = Math.Min(Math.Min(windowRect.Width, windowRect.Height), Math.Max(1, (int)Math.Ceiling(56 * density)));
        var visibleRects = new[]
        {
            new RECT { Left = 0, Top = 0, Right = windowRect.Width, Bottom = toolbarHeight },
            new RECT { Left = windowRect.Width - gripSize, Top = windowRect.Height - gripSize, Right = windowRect.Width, Bottom = windowRect.Height }
        };

        SetWindowRgnFromWindowRects(hwnd, windowRect, windowRect, visibleRects);
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (IsContentChildWindow(child) && GetWindowRect(child, out var childRect))
                SetWindowRgnFromWindowRects(child, windowRect, childRect, visibleRects);
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsContentChildWindow(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        if (GetClassName(hwnd, className, className.Capacity) <= 0)
            return false;

        return className.ToString().Contains("DesktopChildSiteBridge", StringComparison.Ordinal);
    }

    private static void SetWindowRgnFromWindowRects(IntPtr targetHwnd, RECT ownerRect, RECT targetRect, IReadOnlyList<RECT> ownerLocalVisibleRects)
    {
        IntPtr combinedRegion = IntPtr.Zero;
        var targetOffsetX = targetRect.Left - ownerRect.Left;
        var targetOffsetY = targetRect.Top - ownerRect.Top;

        try
        {
            foreach (var visibleRect in ownerLocalVisibleRects)
            {
                var x1 = Math.Max(0, visibleRect.Left - targetOffsetX);
                var y1 = Math.Max(0, visibleRect.Top - targetOffsetY);
                var x2 = Math.Min(targetRect.Width, visibleRect.Right - targetOffsetX);
                var y2 = Math.Min(targetRect.Height, visibleRect.Bottom - targetOffsetY);
                if (x2 <= x1 || y2 <= y1) continue;

                var partRegion = CreateRectRgn(x1, y1, x2, y2);
                if (partRegion == IntPtr.Zero) continue;

                if (combinedRegion == IntPtr.Zero)
                {
                    combinedRegion = partRegion;
                    continue;
                }

                CombineRgn(combinedRegion, combinedRegion, partRegion, RGN_OR);
                DeleteObject(partRegion);
            }

            combinedRegion = combinedRegion == IntPtr.Zero
                ? CreateRectRgn(0, 0, 0, 0)
                : combinedRegion;
            if (combinedRegion != IntPtr.Zero && SetWindowRgn(targetHwnd, combinedRegion, true) != 0)
                combinedRegion = IntPtr.Zero; // owned by the OS after a successful SetWindowRgn call
        }
        finally
        {
            if (combinedRegion != IntPtr.Zero)
                DeleteObject(combinedRegion);
        }
    }
}
