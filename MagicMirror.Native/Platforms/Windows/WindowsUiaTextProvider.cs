using System.Diagnostics;
using System.Windows.Automation;
using MagicMirror.Native.Mirror;
using AutoCondition = System.Windows.Automation.Condition;

namespace MagicMirror.Native.Platforms.Windows;

/// <summary>
/// Reads text + bounding boxes straight from the window under the mirror via Windows UI Automation,
/// so OCR (and a screenshot) can be skipped when the application exposes its text. This is faster
/// and lighter than OCR and yields exact text; it returns an empty list (→ OCR fallback) for
/// windows that don't expose accessible text (games, canvas/GPU-rendered surfaces, etc.).
///
/// Performance: a single cached <see cref="AutomationElement.FindAll"/> over the foreground window
/// batches all property reads into one cross-process round-trip, then results are filtered to the
/// mirror rectangle. Bounded by element count and a wall-clock budget so it never stalls the UI.
/// </summary>
public sealed class WindowsUiaTextProvider : IWindowTextProvider
{
    private const int MaxElements = 1200;
    private const int TimeBudgetMs = 1500;

    public bool IsAvailable => true;

    public Task<IReadOnlyList<OcrTextRegion>> ExtractTextAsync(
        int screenX, int screenY, int width, int height, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<OcrTextRegion>>(() => Extract(screenX, screenY, width, height), ct);

    private static IReadOnlyList<OcrTextRegion> Extract(int sx, int sy, int w, int h)
    {
        var results = new List<OcrTextRegion>();
        try
        {
            int currentPid = Environment.ProcessId;
            // Element under the mirror centre → walk up to its top-level window.
            var centre = new System.Windows.Point(sx + w / 2.0, sy + h / 2.0);
            AutomationElement? el = AutomationElement.FromPoint(centre);
            if (el == null) return results;

            var root = TopLevel(el) ?? el;
            if (IsCurrentProcess(root, currentPid))
            {
                root = FindBestNonMirrorWindow(sx, sy, w, h, currentPid);
                if (root == null)
                {
                    MirrorLog.Info("UIA skipped: mirror window is topmost and no background UIA window was found");
                    return results;
                }
            }

            var cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ProcessIdProperty);
            cache.TreeScope = TreeScope.Element | TreeScope.Descendants;

            AutomationElementCollection found;
            using (cache.Activate())
                found = root.FindAll(TreeScope.Element | TreeScope.Descendants, AutoCondition.TrueCondition);

            var sw = Stopwatch.StartNew();
            double right = sx + w, bottom = sy + h;
            int processed = 0;

            foreach (AutomationElement e in found)
            {
                if (++processed > MaxElements || sw.ElapsedMilliseconds > TimeBudgetMs) break;

                string text;
                System.Windows.Rect r;
                ControlType? ctype;
                bool offscreen;
                try
                {
                    if (e.Cached.ProcessId == currentPid) continue;
                    text = (e.Cached.Name ?? string.Empty).Trim();
                    r = e.Cached.BoundingRectangle;
                    ctype = e.Cached.ControlType;
                    offscreen = e.Cached.IsOffscreen;
                }
                catch { continue; }

                if (offscreen || text.Length < 2 || r.Width < 4 || r.Height < 4) continue;
                if (double.IsInfinity(r.X) || double.IsNaN(r.X)) continue;
                if (!IsTextBearing(ctype)) continue;

                // Skip containers/windows that span well beyond the mirror (not a text line).
                if (r.Width > w * 1.5 && r.Height > h * 1.5) continue;
                // Skip a single element that is itself taller than the whole mirror (a big surface).
                if (r.Height > h * 1.2) continue;

                // Intersect with the mirror rectangle (screen space).
                double l = Math.Max(sx, r.Left), t = Math.Max(sy, r.Top);
                double rr = Math.Min(right, r.Right), bb = Math.Min(bottom, r.Bottom);
                if (rr - l < 3 || bb - t < 3) continue;

                int height = (int)Math.Round(r.Height);
                results.Add(new OcrTextRegion
                {
                    Text = text,
                    X = (int)Math.Round(r.Left - sx),
                    Y = (int)Math.Round(r.Top - sy),
                    Width = (int)Math.Round(r.Width),
                    Height = height,
                    LineHeightHint = height,
                    Confidence = 100f, // exact text from the app
                });
            }
        }
        catch (Exception ex)
        {
            MirrorLog.Error("UIA.Extract", ex);
        }
        return results;
    }

    private static AutomationElement? FindBestNonMirrorWindow(int sx, int sy, int w, int h, int currentPid)
    {
        try
        {
            var cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ProcessIdProperty);
            cache.TreeScope = TreeScope.Element;

            AutomationElementCollection windows;
            using (cache.Activate())
                windows = AutomationElement.RootElement.FindAll(TreeScope.Children, AutoCondition.TrueCondition);

            double right = sx + w, bottom = sy + h;
            AutomationElement? best = null;
            double bestArea = 0;

            foreach (AutomationElement window in windows)
            {
                try
                {
                    if (window.Cached.ProcessId == currentPid || window.Cached.IsOffscreen)
                        continue;

                    var r = window.Cached.BoundingRectangle;
                    if (r.Width < 16 || r.Height < 16 || double.IsNaN(r.X) || double.IsInfinity(r.X))
                        continue;

                    double l = Math.Max(sx, r.Left), t = Math.Max(sy, r.Top);
                    double rr = Math.Min(right, r.Right), bb = Math.Min(bottom, r.Bottom);
                    double area = Math.Max(0, rr - l) * Math.Max(0, bb - t);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = window;
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }

            if (best != null)
                MirrorLog.Info($"UIA target window: pid={best.Cached.ProcessId} name='{best.Cached.Name}'");
            return best;
        }
        catch (Exception ex)
        {
            MirrorLog.Error("UIA.FindBestNonMirrorWindow", ex);
            return null;
        }
    }

    private static bool IsCurrentProcess(AutomationElement element, int currentPid)
    {
        try { return element.Current.ProcessId == currentPid; }
        catch (ElementNotAvailableException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static AutomationElement? TopLevel(AutomationElement el)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var root = AutomationElement.RootElement;
            var cur = el;
            for (int i = 0; i < 40 && cur != null; i++)
            {
                var parent = walker.GetParent(cur);
                if (parent == null || Equals(parent, root)) return cur; // child of desktop = top window
                cur = parent;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Keep only controls that actually carry readable text (skip panes/scrollbars/etc.).</summary>
    private static bool IsTextBearing(ControlType? ct)
    {
        if (ct == null) return true;
        return ct == ControlType.Text || ct == ControlType.Document || ct == ControlType.Edit
            || ct == ControlType.Button || ct == ControlType.Hyperlink || ct == ControlType.ListItem
            || ct == ControlType.TreeItem || ct == ControlType.MenuItem || ct == ControlType.TabItem
            || ct == ControlType.CheckBox || ct == ControlType.RadioButton || ct == ControlType.DataItem
            || ct == ControlType.HeaderItem || ct == ControlType.ComboBox;
    }
}
