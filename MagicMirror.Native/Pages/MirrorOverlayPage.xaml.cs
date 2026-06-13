using Microsoft.Maui.Graphics;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Text.RegularExpressions;
using IImage = Microsoft.Maui.Graphics.IImage;
using MagicMirror.Native.Mirror;

namespace MagicMirror.Native.Pages;

/// <summary>
/// The transparent "glass" overlay window. While idle it shows a live see-through capture of
/// whatever is behind it (so it reads like real glass). On "Translate" it freezes the frame,
/// OCRs + translates the text behind it, and paints the translation over the original with
/// slight dimming. The window itself is excluded from screen capture, so it never mirrors
/// itself nor re-OCRs its own overlay.
/// </summary>
public partial class MirrorOverlayPage : ContentPage
{
    private readonly MirrorEngine _engine;
    private readonly MirrorSettingsStore _settings;
    private readonly MirrorDrawable _drawable = new();
    private IDispatcherTimer? _liveTimer;
    private IDispatcherTimer? _animTimer; // drives the HUD glow + scan beam
    private bool _busy;
    private bool _liveMode = true;
    private bool _dragging; // paused live capture while moving/resizing → smooth drag
    private float _glow;
    private bool _scanning;
    private float _scan;

    private (int X, int Y, int W, int H) _dragStart;
    private (int X, int Y, int W, int H) _resizeStart;

    private static readonly (string Label, string Hex)[] BackgroundPalette =
    {
        ("ورق", "#F3F6EF"),
        ("أبيض", "#FFFFFF"),
        ("عاجي", "#FFF2CC"),
        ("ليل", "#101827"),
        ("سماوي", "#E7F8FF"),
    };

    private static readonly (string Label, string Hex)[] TextPalette =
    {
        ("حبر", "#0D0F14"),
        ("أسود", "#000000"),
        ("أبيض", "#F8FAFC"),
        ("أزرق", "#0B3A75"),
        ("بني", "#4A2A12"),
    };

    private static readonly double[] BackgroundOpacitySteps = { 0.0, 0.25, 0.45, 0.60, 0.72, 0.85, 1.0 };
    private static readonly Regex DictionaryLtrRunRegex = new(@"[A-Za-z0-9][A-Za-z0-9._:/@#%+\-=]*", RegexOptions.Compiled);
    private static readonly string[] DictionaryHeadings =
    {
        "المدخل", "النص المحدد", "مجال النص", "المجال", "التصنيف", "المعنى",
        "البدائل", "الاحتمالات", "الفروق", "السياق", "سبب الترجيح", "الترجيح",
        "الخلاصة", "خلاصة فاصلة", "التوصية", "ملاحظة",
        "Entry", "Domain", "Classification", "Meaning", "Alternatives", "Context",
        "Rationale", "Recommendation", "Summary"
    };

#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? _wheelElement;
    private Microsoft.UI.Xaml.Input.PointerEventHandler? _wheelHandler;
    private Microsoft.UI.Xaml.Input.PointerEventHandler? _pointerPressedHandler;
#endif

    private TranslatedBlock? _selectedDictionaryBlock;
    private string _selectedDictionaryText = "";
    private double _dictionaryPanelStartX;
    private double _dictionaryPanelStartY;

    public MirrorOverlayPage(MirrorEngine engine, MirrorSettingsStore settings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        ApplySettingsToDrawable(settings.Current);
        Canvas.Drawable = _drawable;
        _settings.Changed += OnSettingsChanged;
#if WINDOWS
        Canvas.HandlerChanged += (_, _) => HookMouseWheel();
#endif
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        ConfigureChrome();
        BtnMode.Text = ModeLabel(_drawable.LayoutMode);
        UpdateAppearanceButtons(_settings.Current);
        SetCopyButtonsEnabled(false);
        SetScrollButtonsEnabled(false);
        Canvas.SizeChanged += (_, _) => Canvas.Invalidate(); // adapt overlay to window resize
#if WINDOWS
        HookMouseWheel();
#endif
        StartAnim();
        if (Environment.GetEnvironmentVariable("MM_MANIP_DIAG") == "1")
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), BeginManipulation);
        if (Environment.GetEnvironmentVariable("MM_DIAG") == "1") { RenderSyntheticDiag(); return; }
        StartLive();
    }

    /// <summary>Renders fixed synthetic translated blocks (no capture) to isolate overlay rendering.</summary>
    private void RenderSyntheticDiag()
    {
        _liveMode = false;
        StopLive();
        _drawable.CaptureWidth = 540;
        _drawable.CaptureHeight = 340;
        _drawable.Background = null;
        _drawable.Dim = 0.5;
        _drawable.RightToLeft = true;
        _drawable.ShowIdleHint = false;
        _drawable.Blocks = new List<TranslatedBlock>
        {
            new() { OriginalText = "File", TranslatedText = "ملف", X = 30, Y = 40, Width = 200, Height = 36,
                    Font = new FontInfo { Family = "Segoe UI", SizePt = 20 } },
            new() { OriginalText = "Save changes", TranslatedText = "حفظ التغييرات", X = 30, Y = 110, Width = 360, Height = 40,
                    Font = new FontInfo { Family = "Traditional Arabic", SizePt = 22 } },
            new() { OriginalText = "Settings", TranslatedText = "الإعدادات", X = 30, Y = 190, Width = 240, Height = 38,
                    Font = new FontInfo { Family = "Segoe UI", SizePt = 20 } },
        };
        _drawable.ShowTranslations = true;
        _drawable.LogNextFrame = true;
        _drawable.ContentScrollY = 0;
        Canvas.Invalidate();
        SetCopyButtonsEnabled(true);
        SetScrollButtonsEnabled(true);
        SetStatus("DIAG synthetic");
        MirrorLog.Info("DIAG synthetic render invoked");
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
#if WINDOWS
        UnhookMouseWheel();
        if (Window != null)
            MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.SetManipulationClip(Window, false, DisplayDensity());
#endif
        _settings.Changed -= OnSettingsChanged;
        StopLive();
        StopAnim();
    }

    // ── Futuristic HUD animation (continuous glow pulse + scan beam) ─────────
    private void StartAnim()
    {
        StopAnim();
        _animTimer = Dispatcher.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 fps
        _animTimer.Tick += (_, _) =>
        {
            _glow = (_glow + 0.012f) % 1f;
            _drawable.GlowPhase = _glow;
            if (_scanning)
            {
                _scan = (_scan + 0.035f) % 1f;
                _drawable.ScanProgress = _scan;
            }
            Canvas.Invalidate();
        };
        _animTimer.Start();
    }

    private void StopAnim()
    {
        if (_animTimer != null) { _animTimer.Stop(); _animTimer = null; }
    }

    // ── Live see-through preview ────────────────────────────────────────────
    private void StartLive()
    {
        _liveMode = true;
        double fps = Math.Clamp(_settings.Current.IdlePreviewFps, 0, 30);
        StopLive();
        if (fps <= 0) return;

        _liveTimer = Dispatcher.CreateTimer();
        _liveTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        _liveTimer.Tick += async (_, _) => await LiveTickAsync();
        _liveTimer.Start();
    }

    private void StopLive()
    {
        if (_liveTimer != null) { _liveTimer.Stop(); _liveTimer = null; }
    }

    private async Task LiveTickAsync(bool force = false)
    {
        if (_busy || !_liveMode || (!force && _dragging)) return;
        var (x, y, w, h) = GetRegion();
        if (w <= 0 || h <= 0) return;
        _busy = true;
        try
        {
            SetCaptureExclusion(true);
            var capture = await _engine.CapturePreviewAsync(x, y, w, h);
            SetCaptureExclusion(false);
            if (!capture.IsEmpty && capture.Png != null)
            {
                _drawable.Background = LoadImage(capture.Png);
                _drawable.CaptureWidth = capture.Width;
                _drawable.CaptureHeight = capture.Height;
                _drawable.ShowTranslations = false;
                _drawable.ShowIdleHint = false;
                _drawable.ContentScrollY = 0;
                SetCopyButtonsEnabled(false);
                SetScrollButtonsEnabled(false);
                Canvas.Invalidate();
            }
        }
        catch { }
        finally { _busy = false; }
    }

    // ── Translate ───────────────────────────────────────────────────────────
    private async void OnTranslate(object? sender, EventArgs e) => await TranslateCurrentViewAsync();

    private async Task TranslateCurrentViewAsync()
    {
        if (_busy) return;
        _liveMode = false;
        StopLive();
        var (x, y, w, h) = GetRegion();
        if (w <= 0 || h <= 0) { SetStatus("⚠️ window not ready"); return; }

        _busy = true;
        _scanning = true;
        _drawable.Processing = true;
        SetStatus("⏳ capturing…");
        BtnTranslate.IsEnabled = false;
        try
        {
            SetCaptureExclusion(true);
            var result = await _engine.TranslateRegionAsync(x, y, w, h);
            SetCaptureExclusion(false);
            if (result.Capture.Png != null)
            {
                _drawable.Background = LoadImage(result.Capture.Png);
                _drawable.CaptureWidth = result.Capture.Width;
                _drawable.CaptureHeight = result.Capture.Height;
            }
            ApplySettingsToDrawable(_settings.Current);
            _drawable.SelectedBlock = null;
            _drawable.SelectedText = null;
            _drawable.SelectedTextBounds = null;
            _selectedDictionaryBlock = null;
            _selectedDictionaryText = "";
            DictionaryPanel.IsVisible = false;
            _drawable.Blocks = result.Blocks;
            _drawable.ShowTranslations = true;
            _drawable.ShowIdleHint = false;
            _drawable.ContentScrollY = 0;
            SetCopyButtonsEnabled(result.Blocks.Count > 0);
            SetScrollButtonsEnabled(result.Blocks.Count > 0);
            Canvas.Invalidate();
            MirrorLog.Info($"Overlay render: blocks={result.Blocks.Count} cap={_drawable.CaptureWidth}x{_drawable.CaptureHeight} canvas={Canvas.Width:0}x{Canvas.Height:0}");
            foreach (var b in result.Blocks.Take(4))
                MirrorLog.Info($"  block [{b.X},{b.Y},{b.Width}x{b.Height}] '{Trunc(b.TranslatedText)}' font={b.Font.Family}/{b.Font.SizePt}");
            SetStatus("✦ " + result.Status);
        }
        catch (Exception ex)
        {
            MirrorLog.Error("OnTranslate", ex);
            SetStatus("❌ " + ex.Message);
        }
        finally
        {
            SetCaptureExclusion(false);
            _busy = false;
            _scanning = false;
            _drawable.Processing = false;
            BtnTranslate.IsEnabled = true;
        }
    }

    private static string Trunc(string s, int max = 40) => s.Length <= max ? s : s.Substring(0, max) + "…";

    // ── Clipboard ────────────────────────────────────────────────────────────
    private async void OnCopyOriginal(object? sender, EventArgs e)
        => await CopyBlocksAsync(block => block.OriginalText, "الأصل");

    private async void OnCopyTranslated(object? sender, EventArgs e)
        => await CopyBlocksAsync(block => block.TranslatedText, "الترجمة");

    private async Task CopyBlocksAsync(Func<TranslatedBlock, string> selector, string label)
    {
        try
        {
            var text = BuildClipboardText(selector);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("⚠️ لا يوجد نص للنسخ");
                return;
            }

            await Clipboard.Default.SetTextAsync(text);
            SetStatus($"⧉ تم نسخ {label} ({text.Length} حرف)");
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Copy clipboard", ex);
            SetStatus("❌ تعذر النسخ");
        }
    }

    private string BuildClipboardText(Func<TranslatedBlock, string> selector)
    {
        return string.Join(
            Environment.NewLine,
            _drawable.Blocks
                .Select(selector)
                .Select(text => text.Trim())
                .Where(text => text.Length > 0));
    }

    private void SetCopyButtonsEnabled(bool enabled)
    {
        BtnCopyOriginal.IsEnabled = enabled;
        BtnCopyTranslated.IsEnabled = enabled;
    }

    private void SetScrollButtonsEnabled(bool enabled)
    {
        BtnScrollUp.IsEnabled = enabled;
        BtnScrollDown.IsEnabled = enabled;
    }

    /// <summary>Exclude the overlay from capture only during our own BitBlt (prevents self-capture)
    /// while keeping the translated result visible to ordinary screenshots the rest of the time.</summary>
    private void SetCaptureExclusion(bool on)
    {
#if WINDOWS
        if (Window != null)
            MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.SetExcludedFromCapture(Window, on);
#endif
    }

    private void OnToggleLive(object? sender, EventArgs e)
    {
        if (_liveMode)
        {
            _liveMode = false;
            StopLive();
            _drawable.ContentScrollY = 0;
            SetCopyButtonsEnabled(false);
            SetScrollButtonsEnabled(false);
            SetStatus("⏸ frozen");
        }
        else
        {
            EnterLiveMode("👁 live");
        }
    }

    private async void OnDoubleTapSurface(object? sender, TappedEventArgs e)
    {
        if (_busy || _dragging) return;
        if (_liveMode)
        {
            await TranslateCurrentViewAsync();
            return;
        }

        EnterLiveMode("👁 live");
    }

    private void EnterLiveMode(string status)
    {
        _drawable.ContentScrollY = 0;
        _drawable.ShowTranslations = false;
        _drawable.SelectedBlock = null;
        _drawable.SelectedText = null;
        _drawable.SelectedTextBounds = null;
        _selectedDictionaryBlock = null;
        _selectedDictionaryText = "";
        DictionaryPanel.IsVisible = false;
        SetCopyButtonsEnabled(false);
        SetScrollButtonsEnabled(false);
        SetStatus(status);
        StartLive();
    }

    private void OnClose(object? sender, EventArgs e)
    {
        StopLive();
        if (Window != null) Application.Current?.CloseWindow(Window);
    }

    // ── Text size & arrangement (live re-render — no re-translate) ─────────
    private void OnScrollUp(object? sender, EventArgs e) => AdjustContentScroll(-90);
    private void OnScrollDown(object? sender, EventArgs e) => AdjustContentScroll(+90);

    private void AdjustContentScroll(double delta)
    {
        var max = Math.Max(0, _drawable.MaxContentScrollY);
        var next = Math.Max(0, _drawable.ContentScrollY + delta);
        if (max > 0) next = Math.Min(next, max);
        _drawable.ContentScrollY = next;
        Canvas.Invalidate();
        SetStatus(max > 0 ? $"↕ {_drawable.ContentScrollY:0}/{max:0}" : "↕ لا يوجد امتداد");
    }

#if WINDOWS
    private void HookMouseWheel()
    {
        if (_wheelElement != null) return;
        if (Canvas.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement element) return;

        _wheelElement = element;
        _wheelHandler = OnCanvasPointerWheelChanged;
        _pointerPressedHandler = OnCanvasPointerPressed;
        element.PointerWheelChanged += _wheelHandler;
        element.PointerPressed += _pointerPressedHandler;
    }

    private void UnhookMouseWheel()
    {
        if (_wheelElement != null && _wheelHandler != null)
            _wheelElement.PointerWheelChanged -= _wheelHandler;
        if (_wheelElement != null && _pointerPressedHandler != null)
            _wheelElement.PointerPressed -= _pointerPressedHandler;
        _wheelElement = null;
        _wheelHandler = null;
        _pointerPressedHandler = null;
    }

    private void OnCanvasPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_busy || _dragging || !_drawable.ShowTranslations || _drawable.Blocks.Count == 0)
            return;

        if (sender is not Microsoft.UI.Xaml.UIElement element) return;
        var delta = e.GetCurrentPoint(element).Properties.MouseWheelDelta;
        if (delta == 0) return;

        AdjustContentScroll(delta > 0 ? -120 : 120);
        e.Handled = true;
    }

    private void OnCanvasPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_busy || _dragging || !_drawable.ShowTranslations || _drawable.Blocks.Count == 0)
            return;
        if (sender is not Microsoft.UI.Xaml.UIElement element) return;

        var point = e.GetCurrentPoint(element);
        var pointerKind = point.Properties.PointerUpdateKind.ToString();
        var explicitSelection =
            point.Properties.IsRightButtonPressed ||
            pointerKind.Contains("RightButtonPressed", StringComparison.OrdinalIgnoreCase) ||
            DictionaryPanel.IsVisible;
        var primarySelection =
            point.Properties.IsLeftButtonPressed ||
            pointerKind.Contains("LeftButtonPressed", StringComparison.OrdinalIgnoreCase);

        var hit = FindHitRegionAtCanvasPoint(point.Position.X, point.Position.Y);
        if (hit == null)
        {
            if (explicitSelection)
                e.Handled = true;
            return;
        }
        if (!explicitSelection && !primarySelection)
            return;

        SelectDictionaryHit(hit);
        DictionaryPanel.IsVisible = true;
        ShowDictionaryStatus("انقر «معجم» للحصول على خمسة بدائل وسياق ترجيحي مرتب حسب قواعد المداخل المعجمية.");
        SetStatus($"معجم: {Trunc(_selectedDictionaryText, 38)}");
        Canvas.Invalidate();
        e.Handled = true;
    }
#endif

    private MirrorDrawable.TextHitRegion? FindHitRegionAtCanvasPoint(double x, double y)
    {
        var hits = _drawable.HitRegions;
        for (var i = hits.Count - 1; i >= 0; i--)
        {
            var hit = hits[i];
            if (hit.IsWord && Contains(hit.Bounds, x, y))
                return hit;
        }

        for (var i = hits.Count - 1; i >= 0; i--)
        {
            var hit = hits[i];
            if (!hit.IsWord && Contains(hit.Bounds, x, y))
                return hit;
        }

        MirrorDrawable.TextHitRegion? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (var hit in hits)
        {
            var dx = x - Math.Clamp(x, hit.Bounds.Left, hit.Bounds.Right);
            var dy = y - Math.Clamp(y, hit.Bounds.Top, hit.Bounds.Bottom);
            var distance = dx * dx + dy * dy;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = hit;
            }
        }

        return nearestDistance <= 32 * 32 ? nearest : null;
    }

    private static bool Contains(RectF rect, double x, double y)
        => x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;

    private void SelectDictionaryHit(MirrorDrawable.TextHitRegion hit)
    {
        _selectedDictionaryBlock = hit.Block;
        var fallback = !string.IsNullOrWhiteSpace(hit.Block.TranslatedText) ? hit.Block.TranslatedText : hit.Block.OriginalText;
        _selectedDictionaryText = string.IsNullOrWhiteSpace(hit.Text) ? fallback.Trim() : hit.Text.Trim();
        _drawable.SelectedBlock = hit.Block;
        _drawable.SelectedText = _selectedDictionaryText;
        _drawable.SelectedTextBounds = !hit.IsSource && (hit.IsWord || hit.Bounds.Height <= 90)
            ? hit.Bounds
            : null;
        LblDictionarySelection.Text = $"{Trunc(_selectedDictionaryText, 28)} ⇄ {Trunc(hit.Block.OriginalText, 34)}";
    }

    private async void OnDictionaryExplain(object? sender, EventArgs e)
    {
        if (_selectedDictionaryBlock == null || _busy)
            return;

        _busy = true;
        BtnDictionaryExplain.IsEnabled = false;
        ShowDictionaryStatus("يجري بناء التحليل المعجمي عبر النموذج...");
        try
        {
            var context = BuildDictionaryContext();
            var result = await _engine.Translator.ExplainDictionaryAsync(
                _selectedDictionaryText, context, _settings.Current.TargetLanguage, _settings.Current);
            ShowDictionaryResult(result);
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Dictionary explain", ex);
            ShowDictionaryStatus("تعذر تنفيذ التحليل المعجمي. راجع سجل المرآة لمعرفة سبب بوابة gpt-oss-120b.");
        }
        finally
        {
            _busy = false;
            BtnDictionaryExplain.IsEnabled = true;
        }
        await DictionaryScroll.ScrollToAsync(0, 0, false);
    }

    private void OnDictionaryClose(object? sender, EventArgs e)
    {
        DictionaryPanel.IsVisible = false;
        _selectedDictionaryBlock = null;
        _selectedDictionaryText = "";
        _drawable.SelectedBlock = null;
        _drawable.SelectedText = null;
        _drawable.SelectedTextBounds = null;
        Canvas.Invalidate();
    }

    private void OnDictionaryNudge(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string raw })
            return;

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], out var dx) ||
            !double.TryParse(parts[1], out var dy))
            return;

        MoveDictionaryPanel(dx, dy);
    }

    private void OnDictionaryResetPosition(object? sender, EventArgs e)
    {
        DictionaryPanel.TranslationX = 0;
        DictionaryPanel.TranslationY = 0;
    }

    private void OnDictionaryPanelPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dictionaryPanelStartX = DictionaryPanel.TranslationX;
                _dictionaryPanelStartY = DictionaryPanel.TranslationY;
                break;
            case GestureStatus.Running:
                SetDictionaryPanelTranslation(_dictionaryPanelStartX + e.TotalX, _dictionaryPanelStartY + e.TotalY);
                break;
        }
    }

    private void MoveDictionaryPanel(double dx, double dy)
        => SetDictionaryPanelTranslation(DictionaryPanel.TranslationX + dx, DictionaryPanel.TranslationY + dy);

    private void SetDictionaryPanelTranslation(double x, double y)
    {
        var maxX = Math.Max(120, Canvas.Width - 60);
        var maxY = Math.Max(120, Canvas.Height - 60);
        DictionaryPanel.TranslationX = Math.Clamp(x, -maxX, maxX);
        DictionaryPanel.TranslationY = Math.Clamp(y, -maxY, maxY);
    }

    private string BuildDictionaryContext()
    {
        var blocks = _drawable.Blocks.ToList();
        var selectedIndex = _selectedDictionaryBlock == null ? -1 : blocks.IndexOf(_selectedDictionaryBlock);
        var start = selectedIndex < 0 ? 0 : Math.Max(0, selectedIndex - 3);
        var end = selectedIndex < 0 ? Math.Min(blocks.Count - 1, 6) : Math.Min(blocks.Count - 1, selectedIndex + 3);

        return string.Join(
            Environment.NewLine,
            Enumerable.Range(start, Math.Max(0, end - start + 1)).Select(i =>
            {
                var block = blocks[i];
                return $"{i + 1}. O: {CompactContextLine(block.OriginalText)} | T: {CompactContextLine(block.TranslatedText)}";
            }));
    }

    private static string CompactContextLine(string text)
    {
        var line = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 120 ? line : line.Substring(0, 119).TrimEnd() + "…";
    }

    private void ShowDictionaryStatus(string message)
    {
        DictionaryResultStack.Children.Clear();
        AddDictionarySection("حالة المعجم", new[] { message }, "#14243A", "#3357E3FF");
    }

    private void ShowDictionaryResult(string result)
    {
        DictionaryResultStack.Children.Clear();
        var text = NormalizeDictionaryResult(result);
        if (text.Length == 0)
        {
            ShowDictionaryStatus("لم يرجع النموذج نتيجة معجمية.");
            return;
        }

        var sections = SplitDictionarySections(text).ToList();
        if (sections.Count == 0)
            sections.Add(("نتيجة المعجم", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));

        foreach (var (title, lines) in sections)
        {
            var warning = title.Contains("حالة", StringComparison.OrdinalIgnoreCase) ||
                          lines.Any(line => line.Contains("HTTP ", StringComparison.OrdinalIgnoreCase) ||
                                            line.Contains("غير متاحة", StringComparison.OrdinalIgnoreCase));
            AddDictionarySection(
                title,
                lines,
                warning ? "#2A1E22" : "#14243A",
                warning ? "#66FFB347" : "#3357E3FF");
        }
    }

    private static string NormalizeDictionaryResult(string result)
    {
        var text = (result ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0)
            return "";

        foreach (var heading in DictionaryHeadings)
        {
            text = Regex.Replace(
                text,
                $@"(?<!^)(?<!\n)\s*({Regex.Escape(heading)})\s*[:：]",
                "\n$1:",
                RegexOptions.IgnoreCase);
        }
        for (var i = 1; i <= 9; i++)
        {
            text = text.Replace($" {i}. ", $"\n{i}. ");
            text = text.Replace($" {i}) ", $"\n{i}) ");
        }

        var lines = text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

        return string.Join("\n", lines);
    }

    private static IEnumerable<(string Title, List<string> Lines)> SplitDictionarySections(string text)
    {
        string? title = null;
        var lines = new List<string>();

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TrySplitDictionaryHeading(line, out var nextTitle, out var inline))
            {
                if (title != null && lines.Count > 0)
                    yield return (title, lines);

                title = nextTitle;
                lines = new List<string>();
                if (!string.IsNullOrWhiteSpace(inline))
                    lines.Add(inline);
                continue;
            }

            if (title == null)
                title = LooksLikeAlternativeLine(line) ? "البدائل" : "نتيجة المعجم";
            lines.Add(line);
        }

        if (title != null && lines.Count > 0)
            yield return (title, lines);
    }

    private static bool TrySplitDictionaryHeading(string line, out string title, out string inline)
    {
        title = "";
        inline = "";
        var colon = line.IndexOf(':');
        if (colon < 0)
            colon = line.IndexOf('：');
        if (colon <= 0)
            return false;

        var candidate = line[..colon].Trim();
        if (!DictionaryHeadings.Any(h => string.Equals(candidate, h, StringComparison.OrdinalIgnoreCase)))
            return false;

        title = candidate;
        inline = line[(colon + 1)..].Trim();
        return true;
    }

    private void AddDictionarySection(string title, IEnumerable<string> rawLines, string background, string stroke)
    {
        var card = new Border
        {
            StrokeThickness = 1,
            Padding = new Thickness(9, 7),
            BackgroundColor = Color.FromArgb(background),
            Stroke = Color.FromArgb(stroke)
        };
        var stack = new VerticalStackLayout
        {
            Spacing = 5,
            FlowDirection = FlowDirection.RightToLeft
        };
        stack.Children.Add(new Label
        {
            Text = PrepareDictionaryDisplayText(title),
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9DEBFF"),
            HorizontalTextAlignment = TextAlignment.End,
            LineBreakMode = LineBreakMode.WordWrap,
            LineHeight = 1.22
        });

        foreach (var raw in rawLines.SelectMany(SplitLongDictionaryLine))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var technical = IsMostlyTechnicalDictionaryLine(line);
            stack.Children.Add(new Label
            {
                Text = technical ? line : PrepareDictionaryDisplayText(line),
                FontSize = LooksLikeAlternativeLine(line) ? 11 : 10.5,
                TextColor = Color.FromArgb(technical ? "#E8F6FF" : "#CFEFFF"),
                FlowDirection = technical ? FlowDirection.LeftToRight : FlowDirection.RightToLeft,
                HorizontalTextAlignment = technical ? TextAlignment.Start : TextAlignment.End,
                LineBreakMode = LineBreakMode.WordWrap,
                LineHeight = 1.28,
                Margin = LooksLikeAlternativeLine(line) ? new Thickness(0, 2, 0, 0) : Thickness.Zero
            });
        }

        card.Content = stack;
        DictionaryResultStack.Children.Add(card);
    }

    private static IEnumerable<string> SplitLongDictionaryLine(string line)
    {
        var normalized = (line ?? "").Trim();
        if (normalized.Length == 0)
            yield break;

        foreach (var part in Regex.Split(normalized, @"(?=\b[1-9][\.\)]\s+)"))
        {
            var item = part.Trim();
            if (item.Length > 0)
                yield return item;
        }
    }

    private static string PrepareDictionaryDisplayText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var isolated = DictionaryLtrRunRegex.Replace(text, m => "\u202A" + m.Value + "\u202C");
        return "\u202B" + isolated + "\u202C";
    }

    private static bool LooksLikeAlternativeLine(string line)
        => Regex.IsMatch(line ?? "", @"^\s*(?:[-•]\s*)?[1-9][\.\)]\s+");

    private static bool IsMostlyTechnicalDictionaryLine(string line)
    {
        var text = line ?? "";
        if (text.Length == 0)
            return false;

        var ascii = text.Count(c => c <= 127 && !char.IsWhiteSpace(c));
        var letters = text.Count(char.IsLetterOrDigit);
        return text.Contains("HTTP ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("://", StringComparison.Ordinal) ||
               (letters > 0 && ascii >= Math.Max(4, letters * 2 / 3));
    }

    private void OnSmaller(object? sender, EventArgs e) => AdjustScale(-0.25);
    private void OnBigger(object? sender, EventArgs e) => AdjustScale(+0.25);

    private void OnCycleBackgroundColor(object? sender, EventArgs e)
    {
        var s = _settings.Current;
        s.TranslationBackgroundColor = NextPaletteHex(s.TranslationBackgroundColor, BackgroundPalette);
        _settings.Save(s);
        ApplySettingsToDrawable(s);
        Canvas.Invalidate();
        SetStatus($"▣ الخلفية {PaletteLabel(s.TranslationBackgroundColor, BackgroundPalette)}");
    }

    private void OnCycleBackgroundOpacity(object? sender, EventArgs e)
    {
        var s = _settings.Current;
        s.TranslationBackgroundOpacity = NextOpacity(s.TranslationBackgroundOpacity);
        _settings.Save(s);
        ApplySettingsToDrawable(s);
        Canvas.Invalidate();
        SetStatus($"◌ شفافية الخلفية {s.TranslationBackgroundOpacity * 100:0}%");
    }

    private void OnCycleTextColor(object? sender, EventArgs e)
    {
        var s = _settings.Current;
        s.TranslationTextColor = NextPaletteHex(s.TranslationTextColor, TextPalette);
        _settings.Save(s);
        ApplySettingsToDrawable(s);
        Canvas.Invalidate();
        SetStatus($"حبر {PaletteLabel(s.TranslationTextColor, TextPalette)}");
    }

    private void AdjustScale(double delta)
    {
        var oldScale = _drawable.TextScale;
        var newScale = Math.Clamp(oldScale + delta, 0.75, 3.0);
        if (Math.Abs(newScale - oldScale) < 0.001)
        {
            SetStatus(delta < 0 ? "🔠 أصغر حجم" : "🔠 أكبر حجم");
            return;
        }

        _drawable.TextScale = newScale;
        _drawable.LogNextFrame = true;
        PersistOverlayPrefs();
        Canvas.Invalidate();
        SetStatus($"🔠 {_drawable.TextScale * 100:0}%");
        MirrorLog.Info($"Text scale changed {oldScale:0.00} -> {_drawable.TextScale:0.00}");
    }

    private void OnCycleMode(object? sender, EventArgs e)
    {
        _drawable.LayoutMode = _drawable.LayoutMode switch
        {
            OverlayLayoutMode.MatchOriginal => OverlayLayoutMode.Readable,
            OverlayLayoutMode.Readable => OverlayLayoutMode.Uniform,
            _ => OverlayLayoutMode.MatchOriginal,
        };
        BtnMode.Text = ModeLabel(_drawable.LayoutMode);
        PersistOverlayPrefs();
        Canvas.Invalidate();
        SetStatus("◫ " + ModeLabel(_drawable.LayoutMode));
    }

    private void PersistOverlayPrefs()
    {
        var s = _settings.Current;
        s.TextScale = _drawable.TextScale;
        s.LineSpacingScale = _drawable.LineSpacingScale;
        s.LayoutMode = _drawable.LayoutMode;
        _settings.Save(s);
    }

    private void OnSettingsChanged(object? sender, MirrorSettings settings)
    {
        Dispatcher.Dispatch(() =>
        {
            ApplySettingsToDrawable(settings);
            Canvas.Invalidate();
        });
    }

    private void ApplySettingsToDrawable(MirrorSettings s)
    {
        _drawable.Dim = s.DimAmount;
        _drawable.RightToLeft = IsRtl(s.TargetLanguage);
        _drawable.TextScale = s.TextScale;
        _drawable.LineSpacingScale = s.LineSpacingScale;
        _drawable.LayoutMode = s.LayoutMode;
        _drawable.TranslationBackgroundColor = MirrorAppearanceColors.ToColor(
            s.TranslationBackgroundColor, MirrorAppearanceColors.DefaultBackgroundHex);
        _drawable.TranslationBackgroundOpacity = s.TranslationBackgroundOpacity;
        _drawable.TranslationTextColor = MirrorAppearanceColors.ToColor(
            s.TranslationTextColor, MirrorAppearanceColors.DefaultTextHex);
        UpdateAppearanceButtons(s);
    }

    private void UpdateAppearanceButtons(MirrorSettings s)
    {
        var bg = MirrorAppearanceColors.ToColor(s.TranslationBackgroundColor, MirrorAppearanceColors.DefaultBackgroundHex);
        var ink = MirrorAppearanceColors.ToColor(s.TranslationTextColor, MirrorAppearanceColors.DefaultTextHex);
        BtnBgColor.Text = $"▣ {PaletteLabel(s.TranslationBackgroundColor, BackgroundPalette)}";
        BtnBgColor.BackgroundColor = bg.WithAlpha(0.92f);
        BtnBgColor.TextColor = IsDark(bg) ? Colors.White : Colors.Black;

        BtnBgOpacity.Text = $"◌ {s.TranslationBackgroundOpacity * 100:0}%";

        BtnTextColor.Text = $"حبر {PaletteLabel(s.TranslationTextColor, TextPalette)}";
        BtnTextColor.TextColor = ink;
        BtnTextColor.BorderColor = ink.WithAlpha(0.8f);
    }

    private static string NextPaletteHex(string current, IReadOnlyList<(string Label, string Hex)> palette)
    {
        var normalized = MirrorAppearanceColors.NormalizeHex(current, palette[0].Hex);
        var index = -1;
        for (var i = 0; i < palette.Count; i++)
        {
            if (MirrorAppearanceColors.NormalizeHex(palette[i].Hex, palette[0].Hex) == normalized)
            {
                index = i;
                break;
            }
        }
        return palette[(index + 1 + palette.Count) % palette.Count].Hex;
    }

    private static string PaletteLabel(string current, IReadOnlyList<(string Label, string Hex)> palette)
    {
        var normalized = MirrorAppearanceColors.NormalizeHex(current, palette[0].Hex);
        foreach (var item in palette)
            if (MirrorAppearanceColors.NormalizeHex(item.Hex, palette[0].Hex) == normalized)
                return item.Label;
        return normalized;
    }

    private static double NextOpacity(double current)
    {
        foreach (var step in BackgroundOpacitySteps)
            if (step > current + 0.01)
                return step;
        return BackgroundOpacitySteps[0];
    }

    private static bool IsDark(Color color)
    {
        var luminance = 0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue;
        return luminance < 0.45;
    }

    private static string ModeLabel(OverlayLayoutMode m) => m switch
    {
        OverlayLayoutMode.MatchOriginal => "مطابق",
        OverlayLayoutMode.Uniform => "موحّد",
        _ => "مقروء",
    };

    // ── Drag & resize ───────────────────────────────────────────────────────
    private void OnDragSurface(object? sender, PanUpdatedEventArgs e)
    {
        if (DictionaryPanel.IsVisible || _selectedDictionaryBlock != null)
            return;

        OnDragToolbar(sender, e);
    }

    private void OnDragToolbar(object? sender, PanUpdatedEventArgs e)
    {
        if (ReferenceEquals(sender, Canvas) && (DictionaryPanel.IsVisible || _selectedDictionaryBlock != null))
            return;

        double density = DisplayDensity();
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dragging = true;
                BeginManipulation();
                _dragStart = GetRegion();
                break;
            case GestureStatus.Running:
                int nx = _dragStart.X + (int)Math.Round(e.TotalX * density);
                int ny = _dragStart.Y + (int)Math.Round(e.TotalY * density);
                MoveResize(nx, ny, _dragStart.W, _dragStart.H);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                EndManipulation();
                break;
        }
    }

    private void OnResizeGrip(object? sender, PanUpdatedEventArgs e)
    {
        double density = DisplayDensity();
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dragging = true;
                BeginManipulation();
                _resizeStart = GetRegion();
                break;
            case GestureStatus.Running:
                int nw = Math.Max(140, _resizeStart.W + (int)Math.Round(e.TotalX * density));
                int nh = Math.Max(90, _resizeStart.H + (int)Math.Round(e.TotalY * density));
                MoveResize(_resizeStart.X, _resizeStart.Y, nw, nh);
#if WINDOWS
                if (Window != null)
                    MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.SetManipulationClip(Window, true, density);
#endif
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                EndManipulation();
                break;
        }
    }

    /// <summary>Drag/resize ended — resume the live preview with a single fresh frame.</summary>
    private async void EndManipulation()
    {
        _drawable.ManipulatingGlass = false;
        _dragging = false;
        Canvas.Invalidate();

        try
        {
            for (var i = 0; _busy && i < 6; i++)
                await Task.Delay(16);

            if (_liveMode)
                await LiveTickAsync(force: true);

            await Task.Delay(50);
        }
        catch (Exception ex)
        {
            MirrorLog.Error("EndManipulation refresh", ex);
        }
        finally
        {
#if WINDOWS
            if (Window != null)
                MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.SetManipulationClip(Window, false, DisplayDensity());
#endif
            Canvas.Invalidate();
        }
    }

    private void BeginManipulation()
    {
#if WINDOWS
        if (Window != null)
            MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.SetManipulationClip(Window, true, DisplayDensity());
#endif
        _drawable.ManipulatingGlass = true;
        Canvas.Invalidate();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private void SetStatus(string text) => LblStatus.Text = text;

    private static bool IsRtl(string lang) =>
        lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ||
        lang.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ||
        lang.StartsWith("ur", StringComparison.OrdinalIgnoreCase) ||
        lang.StartsWith("he", StringComparison.OrdinalIgnoreCase);

    private static IImage? LoadImage(byte[] png)
    {
        try { return Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(new MemoryStream(png)); }
        catch { return null; }
    }

    private double DisplayDensity()
    {
        try { return DeviceDisplay.MainDisplayInfo.Density <= 0 ? 1.0 : DeviceDisplay.MainDisplayInfo.Density; }
        catch { return 1.0; }
    }

    private (int X, int Y, int W, int H) GetRegion()
    {
#if WINDOWS
        if (Window != null)
            return MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.GetScreenRect(Window);
#endif
        return (0, 0, 0, 0);
    }

    private void ConfigureChrome()
    {
#if WINDOWS
        if (Window != null)
            MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.ConfigureOverlay(Window);
#endif
    }

    private void MoveResize(int x, int y, int w, int h)
    {
#if WINDOWS
        if (Window != null)
            MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.MoveResize(Window, x, y, w, h);
#endif
    }
}
