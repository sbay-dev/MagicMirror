using Microsoft.Maui.Graphics;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls.Shapes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private enum TranslationFallbackDecision
    {
        Continue,
        UseMachineTranslation,
        RetrySarmad
    }

    private readonly MirrorEngine _engine;
    private readonly MirrorSettingsStore _settings;
    private readonly GlossaryMemoryStore _glossaryMemory;
    private readonly MirrorDrawable _drawable = new();
    private readonly SelectionOverlayDrawable _selectionOverlay = new();
    private IDispatcherTimer? _liveTimer;
    private IDispatcherTimer? _animTimer; // drives the HUD glow + scan beam
    private IDispatcherTimer? _typewriterTimer;
    private IDispatcherTimer? _wheelResizeEndTimer;
    private CancellationTokenSource? _translationCts;
    private bool _busy;
    private bool _liveMode = true;
    private bool _dragging; // paused live capture while moving/resizing → smooth drag
    private bool _wheelResizing;
    private bool _readerAutoOpenedThisTranslation;
    private double _readerFontSize = 17;
    private string _readerLastText = "";
    private string _nativeOverlayLastText = "";
    private List<ReaderTextSegment> _readerSegments = new();
    private string _selectedDictionaryProof = "";
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
    private const int TypewriterCharsPerTick = 1;
    private const double WheelResizeStep = 1.055;
    private const double PinchMinFactor = 0.45;
    private const double PinchMaxFactor = 2.35;
    private static readonly string[] DictionaryHeadings =
    {
        "المدخل", "النص المحدد", "مجال النص", "المجال", "التصنيف", "المعنى",
        "البدائل", "الاحتمالات", "الفروق", "السياق", "سبب الترجيح", "الترجيح",
        "الخلاصة", "خلاصة فاصلة", "التوصية", "ملاحظة", "التحليل", "الوظيفة",
        "المُدخل القاموسي", "المدخل القاموسي", "بدائل مختصرة", "بدائل مقترحة",
        "Entry", "Domain", "Classification", "Meaning", "Alternatives", "Context",
        "Rationale", "Recommendation", "Summary"
    };

#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? _wheelElement;
    private Microsoft.UI.Xaml.Input.PointerEventHandler? _wheelHandler;
    private Microsoft.UI.Xaml.Input.PointerEventHandler? _pointerPressedHandler;
#endif

    private TranslatedBlock? _selectedDictionaryBlock;
    private MirrorDrawable.TextHitRegion? _lastDictionaryHit;
    private string _selectedDictionaryText = "";
    private double _dictionaryPanelStartX;
    private double _dictionaryPanelStartY;
    private List<TranslatedBlock> _typewriterBlocks = new();
    private string[] _typewriterTargets = Array.Empty<string>();
    private string _typewriterStatus = "";

    private enum DictionarySelectionScope
    {
        Word,
        Sentence,
    }

    private sealed record ReaderTextSegment(int Start, int Length, TranslatedBlock Block);

    private sealed class SelectionOverlayDrawable : IDrawable
    {
        public RectF? SourceBounds { get; set; }
        public RectF? TextBounds { get; set; }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (SourceBounds is RectF source && source.Width > 0 && source.Height > 0)
            {
                canvas.FillColor = Color.FromRgba(255, 214, 10, 52);
                canvas.FillRoundedRectangle(source, 4);
                canvas.StrokeSize = 2.4f;
                canvas.StrokeColor = Color.FromRgba(255, 214, 10, 230);
                canvas.DrawRoundedRectangle(source, 4);
            }

            if (TextBounds is RectF text && text.Width > 0 && text.Height > 0)
            {
                canvas.FillColor = Color.FromRgba(34, 229, 255, 44);
                canvas.FillRoundedRectangle(text, 4);
                canvas.StrokeSize = 1.8f;
                canvas.StrokeColor = Color.FromRgba(34, 229, 255, 190);
                canvas.DrawRoundedRectangle(text, 4);
            }
        }
    }

    public MirrorOverlayPage(MirrorEngine engine, MirrorSettingsStore settings, GlossaryMemoryStore glossaryMemory)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _glossaryMemory = glossaryMemory;
        ApplySettingsToDrawable(settings.Current);
        Canvas.Drawable = _drawable;
        SelectionOverlay.Drawable = _selectionOverlay;
        _settings.Changed += OnSettingsChanged;
        NativeOverlayEditor.HandlerChanged += (_, _) => ConfigureEditorContextMenu(NativeOverlayEditor, "طبقة المرآة");
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
        _drawable.ShowTranslations = false;
        _drawable.LogNextFrame = true;
        _drawable.ContentScrollY = 0;
        UpdateNativeOverlay(_drawable.Blocks, _drawable.RightToLeft);
        NativeOverlayEditor.IsVisible = true;
        Canvas.Invalidate();
        SetCopyButtonsEnabled(true);
        SetScrollButtonsEnabled(false);
        SetStatus("DIAG synthetic");
        MirrorLog.Info("DIAG synthetic render invoked");
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        StopWheelResizeTimer();
#if WINDOWS
        UnhookMouseWheel();
        if (Window != null)
            MagicMirror.Native.Platforms.Windows.MirrorWindowInterop.SetManipulationClip(Window, false, DisplayDensity());
#endif
        _settings.Changed -= OnSettingsChanged;
        CancelTranslation();
        StopLive();
        StopAnim();
        StopTypewriter(clear: true);
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
            var capture = force
                ? await _engine.CaptureSettledPreviewAsync(x, y, w, h)
                : await _engine.CapturePreviewAsync(x, y, w, h);
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
        CancelTranslation();
        _translationCts = new CancellationTokenSource();
        var cts = _translationCts;
        var ct = cts.Token;
        StopTypewriter(clear: true);
        _readerAutoOpenedThisTranslation = false;
        SetStatus("⏳ capturing…");
        BtnTranslate.IsEnabled = false;
        try
        {
            var forceMachineTranslation = false;
            while (true)
            {
                var decision = await RunProgressiveTranslationAttemptAsync(x, y, w, h, forceMachineTranslation, ct);
                if (decision == TranslationFallbackDecision.UseMachineTranslation)
                {
                    forceMachineTranslation = true;
                    SetStatus("↻ تشغيل MT كبديل صريح للوثيقة كاملة…");
                    continue;
                }
                if (decision == TranslationFallbackDecision.RetrySarmad)
                {
                    forceMachineTranslation = false;
                    SetStatus("↻ إعادة المحاولة بسرمد فقط…");
                    continue;
                }
                break;
            }
            MirrorLog.Info($"Overlay streaming render: blocks={_drawable.Blocks.Count} cap={_drawable.CaptureWidth}x{_drawable.CaptureHeight} canvas={Canvas.Width:0}x{Canvas.Height:0}");
            foreach (var b in _drawable.Blocks.Take(4))
                MirrorLog.Info($"  block [{b.X},{b.Y},{b.Width}x{b.Height}] '{Trunc(b.TranslatedText)}' font={b.Font.Family}/{b.Font.SizePt}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("⏹ أُلغيت الترجمة");
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
            if (ReferenceEquals(_translationCts, cts))
            {
                _translationCts.Dispose();
                _translationCts = null;
            }
        }
    }

    private async Task<TranslationFallbackDecision> RunProgressiveTranslationAttemptAsync(
        int x,
        int y,
        int w,
        int h,
        bool forceMachineTranslation,
        CancellationToken ct)
    {
        var runSettings = BuildTranslationRunSettings(forceMachineTranslation);
        var initialized = false;
        var promptedForFallback = false;
        SetCaptureExclusion(true);
        await foreach (var result in _engine.TranslateRegionProgressiveAsync(x, y, w, h, runSettings, ct))
        {
            ct.ThrowIfCancellationRequested();
            SetCaptureExclusion(false);
            if (result.Capture.Png != null)
            {
                _drawable.Background = LoadImage(result.Capture.Png);
                _drawable.CaptureWidth = result.Capture.Width;
                _drawable.CaptureHeight = result.Capture.Height;
            }
            ApplySettingsToDrawable(_settings.Current);
            if (!initialized)
            {
                _drawable.SelectedBlock = null;
                _drawable.SelectedText = null;
                _drawable.SelectedTextBounds = null;
                _drawable.SelectedTextIsSource = false;
                _drawable.SelectedSourceTextBounds = null;
                SetSelectionOverlay(null, null);
                _selectedDictionaryBlock = null;
                _lastDictionaryHit = null;
                _selectedDictionaryText = "";
                _selectedDictionaryProof = "";
                DictionaryPanel.IsVisible = false;
                _drawable.ContentScrollY = 0;
                initialized = true;
            }
            UpdateReaderPanel(result);
            _readerAutoOpenedThisTranslation = result.Blocks.Count > 0;

            StopTypewriter(clear: true);
            _drawable.Blocks = result.Blocks;

            ApplyReaderVisibility();
            _drawable.ShowIdleHint = false;
            SetCopyButtonsEnabled(result.Blocks.Count > 0);
            SetScrollButtonsEnabled(false);
            SetTranslationSource(result.TranslationSource, result.TranslationSourceLabel);
            SetStatus("⇢ " + result.Status);
            Canvas.Invalidate();

            if (!forceMachineTranslation &&
                !promptedForFallback &&
                ShouldPromptForFallbackSource(result))
            {
                promptedForFallback = true;
                var decision = await AskTranslationFallbackDecisionAsync(result);
                if (decision != TranslationFallbackDecision.Continue)
                    return decision;
            }
        }

        return TranslationFallbackDecision.Continue;
    }

    private MirrorSettings BuildTranslationRunSettings(bool forceMachineTranslation)
    {
        var current = _settings.Current;
        return new MirrorSettings
        {
            SettingsSchemaVersion = current.SettingsSchemaVersion,
            TargetLanguage = current.TargetLanguage,
            SourceLanguageHint = current.SourceLanguageHint,
            TesseractLanguages = current.TesseractLanguages,
            DimAmount = current.DimAmount,
            TranslationBackgroundColor = current.TranslationBackgroundColor,
            TranslationBackgroundOpacity = current.TranslationBackgroundOpacity,
            TranslationTextColor = current.TranslationTextColor,
            TextScale = current.TextScale,
            LineSpacingScale = current.LineSpacingScale,
            LayoutMode = current.LayoutMode,
            IdlePreviewFps = current.IdlePreviewFps,
            OcrEngine = current.OcrEngine,
            OcrCaptureScale = current.OcrCaptureScale,
            UseWindowText = current.UseWindowText,
            GatewayBaseUrl = current.GatewayBaseUrl,
            FallbackSarmadUrl = current.FallbackSarmadUrl,
            AiModel = current.AiModel,
            AllowMachineTranslationFallback = forceMachineTranslation,
            ForceMachineTranslationFallback = forceMachineTranslation,
            TesseractExePath = current.TesseractExePath,
            TessDataPath = current.TessDataPath
        };
    }

    private bool ShouldPromptForFallbackSource(MirrorResult result)
    {
        if (result.Blocks.Count == 0 ||
            string.Equals(result.TranslationSourceLabel, "streaming", StringComparison.OrdinalIgnoreCase))
            return false;

        return result.TranslationSource == TranslationSourceKind.OriginalTextFallback ||
               result.TranslationSource == TranslationSourceKind.Mixed ||
               result.Blocks.Any(block =>
                   block.TranslationSource == TranslationSourceKind.OriginalTextFallback &&
                   !string.IsNullOrWhiteSpace(block.OriginalText) &&
                   !string.IsNullOrWhiteSpace(block.TranslatedText) &&
                   block.TranslatedText.Trim() != "…");
    }

    private async Task<TranslationFallbackDecision> AskTranslationFallbackDecisionAsync(MirrorResult result)
    {
        var source = string.IsNullOrWhiteSpace(result.TranslationSourceLabel)
            ? "Sarmad"
            : result.TranslationSourceLabel;
        var choice = await DisplayActionSheet(
            "سرمد لم يوفر ترجمة موثقة لكل النص",
            "إبقاء النتيجة الحالية",
            null,
            "استخدام MT للوثيقة كاملة (غير أكاديمي)",
            "إعادة محاولة سرمد فقط");

        if (string.Equals(choice, "استخدام MT للوثيقة كاملة (غير أكاديمي)", StringComparison.Ordinal))
        {
            MirrorLog.Info($"User approved explicit MT fallback after source={source}");
            return TranslationFallbackDecision.UseMachineTranslation;
        }
        if (string.Equals(choice, "إعادة محاولة سرمد فقط", StringComparison.Ordinal))
            return TranslationFallbackDecision.RetrySarmad;

        MirrorLog.Info($"User kept Sarmad/original-only result after source={source}");
        return TranslationFallbackDecision.Continue;
    }

    private void CancelTranslation()
    {
        if (_translationCts == null)
            return;

        _translationCts.Cancel();
        _translationCts.Dispose();
        _translationCts = null;
    }

    private static string Trunc(string s, int max = 40) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private void QueueTypewriterResult(MirrorResult result)
    {
        _typewriterStatus = "⇢ " + result.Status;
        if (_typewriterBlocks.Count != result.Blocks.Count)
        {
            _typewriterBlocks = result.Blocks.Select(block => CloneBlockForTypewriter(block, InitialTypewriterText(block.TranslatedText))).ToList();
            _typewriterTargets = result.Blocks.Select(block => NormalizeTypewriterTarget(block.TranslatedText)).ToArray();
            _drawable.Blocks = _typewriterBlocks;
            StartTypewriter();
            return;
        }

        var nextTargets = new string[result.Blocks.Count];
        for (var i = 0; i < result.Blocks.Count; i++)
        {
            var source = result.Blocks[i];
            var visible = _typewriterBlocks[i];
            var target = NormalizeTypewriterTarget(source.TranslatedText);
            nextTargets[i] = target;

            visible.OriginalText = source.OriginalText;
            visible.TranslatedText = ReconcileVisibleTypewriterText(visible.TranslatedText, target);
            visible.TranslationSource = source.TranslationSource;
            visible.TranslationSourceLabel = source.TranslationSourceLabel;
        }

        _typewriterTargets = nextTargets;
        _drawable.Blocks = _typewriterBlocks;
        StartTypewriter();
    }

    private void StartTypewriter()
    {
        if (_typewriterTimer != null)
            return;

        _typewriterTimer = Dispatcher.CreateTimer();
        _typewriterTimer.Interval = TimeSpan.FromMilliseconds(18);
        _typewriterTimer.Tick += (_, _) => TypewriterTick();
        _typewriterTimer.Start();
    }

    private void StopTypewriter(bool clear)
    {
        if (_typewriterTimer != null)
        {
            _typewriterTimer.Stop();
            _typewriterTimer = null;
        }

        if (clear)
        {
            _typewriterBlocks = new List<TranslatedBlock>();
            _typewriterTargets = Array.Empty<string>();
            _typewriterStatus = "";
        }
    }

    private void TypewriterTick()
    {
        var changed = false;
        for (var step = 0; step < TypewriterCharsPerTick; step++)
        {
            var index = -1;
            for (var i = 0; i < _typewriterTargets.Length && i < _typewriterBlocks.Count; i++)
            {
                if (_typewriterBlocks[i].TranslatedText.Length < _typewriterTargets[i].Length)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                break;

            var current = _typewriterBlocks[index].TranslatedText;
            _typewriterBlocks[index].TranslatedText = AppendNextTextElement(current, _typewriterTargets[index]);
            changed = true;
        }

        if (changed)
        {
            SetStatus(_typewriterStatus);
            Canvas.Invalidate();
        }
        else
        {
            StopTypewriter(clear: false);
        }
    }

    private static TranslatedBlock CloneBlockForTypewriter(TranslatedBlock source, string translatedText) => new()
    {
        OriginalText = source.OriginalText,
        TranslatedText = translatedText,
        X = source.X,
        Y = source.Y,
        Width = source.Width,
        Height = source.Height,
        LineHeightHint = source.LineHeightHint,
        Font = source.Font,
        Confidence = source.Confidence,
        SourceLines = source.SourceLines,
        TranslationSource = source.TranslationSource,
        TranslationSourceLabel = source.TranslationSourceLabel,
    };

    private static string InitialTypewriterText(string text)
    {
        var target = NormalizeTypewriterTarget(text);
        return target == "…" ? "…" : "";
    }

    private static string NormalizeTypewriterTarget(string text)
        => string.IsNullOrWhiteSpace(text) ? "" : text.Trim();

    private static string ReconcileVisibleTypewriterText(string visible, string target)
    {
        if (target == "…")
            return string.IsNullOrEmpty(visible) ? "…" : visible;
        if (visible == "…")
            return "";
        return target.StartsWith(visible, StringComparison.Ordinal) ? visible : "";
    }

    private static string AppendNextTextElement(string current, string target)
    {
        if (current.Length >= target.Length)
            return current;

        var next = StringInfo.GetNextTextElement(target, current.Length);
        return target[..Math.Min(target.Length, current.Length + next.Length)];
    }

    private int TypewriterIndexOf(TranslatedBlock block)
        => _typewriterBlocks.FindIndex(candidate => ReferenceEquals(candidate, block));

    private string GetTypewriterTargetOrVisible(TranslatedBlock block)
    {
        var index = TypewriterIndexOf(block);
        return index >= 0 && index < _typewriterTargets.Length
            ? _typewriterTargets[index]
            : block.TranslatedText;
    }

    private void SetVisibleTranslationAndTypewriterTarget(TranslatedBlock block, string visibleTranslation, string? targetTranslation = null)
    {
        block.TranslatedText = visibleTranslation;

        var index = TypewriterIndexOf(block);
        if (index >= 0 && index < _typewriterTargets.Length)
            _typewriterTargets[index] = NormalizeTypewriterTarget(targetTranslation ?? visibleTranslation);
    }

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
        NativeOverlayEditor.IsVisible = false;
        NativeOverlayEditor.Text = "";
        _nativeOverlayLastText = "";
        _drawable.SelectedBlock = null;
        _drawable.SelectedText = null;
        _drawable.SelectedTextBounds = null;
        _drawable.SelectedTextIsSource = false;
        _drawable.SelectedSourceTextBounds = null;
        SetSelectionOverlay(null, null);
        _selectedDictionaryBlock = null;
        _lastDictionaryHit = null;
        _selectedDictionaryText = "";
        _selectedDictionaryProof = "";
        DictionaryPanel.IsVisible = false;
        ReaderPanel.IsVisible = false;
        ReaderEditor.Text = "";
        _readerLastText = "";
        ReaderSourceDetails.Text = "الأصل المعرفي: —";
        BtnReader.Text = "فصل القارئ";
        SourceBadge.IsVisible = false;
        LblTranslationSource.Text = "مصدر الترجمة: —";
        SetCopyButtonsEnabled(false);
        SetScrollButtonsEnabled(false);
        SetStatus(status);
        StartLive();
    }

    private void OnClose(object? sender, EventArgs e)
    {
        StopWheelResizeTimer();
        StopLive();
        if (Window != null) Application.Current?.CloseWindow(Window);
    }

    private void OnToggleReaderPanel(object? sender, EventArgs e)
    {
        if (_liveMode || _drawable.Blocks.Count == 0)
        {
            SetStatus("لا توجد ترجمة في القارئ بعد");
            return;
        }

        ReaderPanel.IsVisible = false;
        ApplyReaderVisibility();
        OpenDetachedReader();
    }

    private void OnCloseReaderPanel(object? sender, EventArgs e)
    {
        SetReaderVisible(false);
        SetStatus("طبقة محرر شفافة فوق الأصل");
    }

    private void OnReaderSmaller(object? sender, EventArgs e)
    {
        AdjustNativeTextFont(-1);
    }

    private void OnReaderBigger(object? sender, EventArgs e)
    {
        AdjustNativeTextFont(+1);
    }

    private void OnDetachReader(object? sender, EventArgs e)
    {
        OpenDetachedReader();
    }

    private void OpenDetachedReader()
    {
        var text = ReaderEditor.Text ?? BuildNativeReaderText(_drawable.Blocks, IsRtl(_settings.Current.TargetLanguage));
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("لا يوجد نص لفصله");
            return;
        }

        var rtl = IsRtl(_settings.Current.TargetLanguage);
        var s = _settings.Current;
        var readerBgHex = s.TranslationBackgroundColor;
        var readerInkHex = s.TranslationTextColor;
        var readerOpacity = Math.Clamp(s.TranslationBackgroundOpacity, 0, 1);
        var readerFontSize = _readerFontSize;
        var readerWrap = true;
        var editor = new Editor
        {
            Text = text,
            IsReadOnly = true,
            AutoSize = EditorAutoSizeOption.Disabled,
            FontFamily = "Segoe UI",
            FontSize = readerFontSize,
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            HorizontalTextAlignment = TextAlignment.Start,
            VerticalTextAlignment = TextAlignment.Start
        };
        var title = new Label
        {
            Text = "قارئ Magic Mirror",
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            TextColor = Color.FromArgb("#1D2430"),
            LineBreakMode = LineBreakMode.TailTruncation
        };
        var btnSmaller = CreateReaderControlButton("A−");
        var btnBigger = CreateReaderControlButton("A+");
        var btnBackground = CreateReaderControlButton("▣");
        var btnOpacity = CreateReaderControlButton("◌");
        var btnInk = CreateReaderControlButton("حبر");
        var btnWrap = CreateReaderControlButton("التفاف");
        var btnDictionary = CreateReaderControlButton("معجم");
        var btnCopy = CreateReaderControlButton("⧉ نسخ");
        var toolbar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 6,
            Padding = new Thickness(0, 0, 0, 8)
        };
        toolbar.Add(title, 0, 0);
        toolbar.Add(btnSmaller, 1, 0);
        toolbar.Add(btnBigger, 2, 0);
        toolbar.Add(btnBackground, 3, 0);
        toolbar.Add(btnOpacity, 4, 0);
        toolbar.Add(btnInk, 5, 0);
        toolbar.Add(btnWrap, 6, 0);
        toolbar.Add(btnDictionary, 7, 0);
        toolbar.Add(btnCopy, 8, 0);
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            Padding = 18
        };
        root.Add(toolbar, 0, 0);
        root.Add(editor, 0, 1);
        var page = new ContentPage
        {
            Title = "Magic Mirror Reader",
            Content = root
        };
        void ApplyDetachedAppearance()
        {
            var bg = MirrorAppearanceColors.ToColor(readerBgHex, MirrorAppearanceColors.DefaultBackgroundHex);
            var ink = MirrorAppearanceColors.ToColor(readerInkHex, MirrorAppearanceColors.DefaultTextHex);
            var surface = bg.WithAlpha((float)Math.Clamp(readerOpacity, 0.05, 1.0));
            var buttonBg = bg.WithAlpha(0.92f);
            var buttonInk = IsDark(bg) ? Colors.White : Colors.Black;
            page.BackgroundColor = bg;
            root.BackgroundColor = surface;
            editor.BackgroundColor = surface;
            editor.TextColor = ink;
            editor.FontSize = readerFontSize;
            title.TextColor = IsDark(bg) ? Colors.White : Color.FromArgb("#1D2430");
            btnBackground.Text = $"▣ {PaletteLabel(readerBgHex, BackgroundPalette)}";
            btnOpacity.Text = $"◌ {readerOpacity * 100:0}%";
            btnInk.Text = $"حبر {PaletteLabel(readerInkHex, TextPalette)}";
            btnWrap.Text = readerWrap ? "التفاف ✓" : "بلا التفاف";
            foreach (var button in new[] { btnSmaller, btnBigger, btnBackground, btnOpacity, btnInk, btnWrap, btnDictionary, btnCopy })
            {
                button.BackgroundColor = buttonBg;
                button.TextColor = button == btnInk ? ink : buttonInk;
                button.BorderColor = ink.WithAlpha(0.65f);
            }
            SetDetachedReaderWrap(editor, readerWrap);
        }
        btnSmaller.Clicked += (_, _) =>
        {
            readerFontSize = Math.Max(12, readerFontSize - 1);
            ApplyDetachedAppearance();
        };
        btnBigger.Clicked += (_, _) =>
        {
            readerFontSize = Math.Min(34, readerFontSize + 1);
            ApplyDetachedAppearance();
        };
        btnBackground.Clicked += (_, _) =>
        {
            readerBgHex = NextPaletteHex(readerBgHex, BackgroundPalette);
            ApplyDetachedAppearance();
        };
        btnOpacity.Clicked += (_, _) =>
        {
            readerOpacity = NextOpacity(readerOpacity);
            ApplyDetachedAppearance();
        };
        btnInk.Clicked += (_, _) =>
        {
            readerInkHex = NextPaletteHex(readerInkHex, TextPalette);
            ApplyDetachedAppearance();
        };
        btnWrap.Clicked += (_, _) =>
        {
            readerWrap = !readerWrap;
            ApplyDetachedAppearance();
        };
        btnDictionary.Clicked += (_, _) => SelectDictionaryFromEditor(editor, "القارئ المفصول");
        btnCopy.Clicked += async (_, _) =>
        {
            await Clipboard.Default.SetTextAsync(editor.Text ?? "");
            SetStatus("⧉ تم نسخ نص القارئ");
        };
        editor.HandlerChanged += (_, _) =>
        {
            SetDetachedReaderWrap(editor, readerWrap);
            ConfigureEditorContextMenu(editor, "القارئ المفصول");
        };
        ApplyDetachedAppearance();
        var window = new Window(page)
        {
            Title = "Magic Mirror Reader",
            Width = 820,
            Height = 900
        };
        Application.Current?.OpenWindow(window);
        SetStatus("فُصل القارئ في نافذة مستقلة");
    }

    private void OnDictionaryFromSelection(object? sender, EventArgs e)
        => SelectDictionaryFromEditor(NativeOverlayEditor, "طبقة المرآة");

    private void SelectDictionaryFromEditor(Editor editor, string surface)
    {
        if (!TryGetEditorSelection(editor, out var start, out var length, out var selected))
        {
            SetStatus("ظلّل كلمة أو جملة في النص أولًا ثم اضغط «معجم المحدد»");
            return;
        }

        var block = FindReaderSegmentBlock(start, length, selected);
        if (block == null)
        {
            SetStatus("تعذر ربط التحديد بكتلة OCR أصلية");
            return;
        }

        var sourceBounds = ResolveSourceCanvasBounds(block);
        _selectedDictionaryBlock = block;
        _selectedDictionaryText = LimitSelectionText(selected);
        _selectedDictionaryProof = BuildDictionaryProof(_selectedDictionaryText, block, start, length);
        _lastDictionaryHit = new MirrorDrawable.TextHitRegion(
            block,
            _selectedDictionaryText,
            sourceBounds ?? RectF.Zero,
            IsWord: false,
            IsSource: false,
            Scope: MirrorDrawable.TextHitScope.Line);
        _drawable.SelectedBlock = block;
        _drawable.SelectedText = _selectedDictionaryText;
        _drawable.SelectedTextBounds = null;
        _drawable.SelectedTextIsSource = false;
        _drawable.SelectedSourceTextBounds = sourceBounds;
        SetSelectionOverlay(null, sourceBounds);
        LblDictionarySelection.Text = $"محدد: {Trunc(_selectedDictionaryText, 72)} · مصدر: {BlockSourceLabel(block)}";
        BtnDictionarySentence.Text = "جملة";
        DictionaryPanel.IsVisible = true;
        ShowDictionaryStatus(
            $"تم ربط تحديد {surface} بكتلة OCR الأصلية.\n" +
            "اضغط «معجم» لتحليل المصطلح مع مسار الإثبات.\n\n" +
            _selectedDictionaryProof);
        Canvas.Invalidate();
        SelectionOverlay.Invalidate();
        SetStatus($"معجم المحدد: {Trunc(_selectedDictionaryText, 38)}");
    }

    private static bool TryGetEditorSelection(Editor editor, out int start, out int length, out string selected)
    {
        var text = editor.Text ?? "";
#if WINDOWS
        if (editor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
        {
            text = textBox.Text ?? text;
            start = Math.Clamp(textBox.SelectionStart, 0, text.Length);
            length = Math.Clamp(textBox.SelectionLength, 0, text.Length - start);
            selected = (textBox.SelectedText ?? "").Trim();
            if (length > 0 && selected.Length == 0)
                selected = text.Substring(start, length).Trim();
            return length > 0 && selected.Length > 0;
        }
#endif
        start = Math.Clamp(editor.CursorPosition, 0, text.Length);
        length = Math.Clamp(editor.SelectionLength, 0, text.Length - start);
        selected = "";
        if (length <= 0)
            return false;

        selected = text.Substring(start, length).Trim();
        return selected.Length > 0;
    }

    private void SetSelectionOverlay(RectF? textBounds, RectF? sourceBounds)
    {
        _selectionOverlay.TextBounds = textBounds;
        _selectionOverlay.SourceBounds = sourceBounds;
        SelectionOverlay.Invalidate();
    }

    private TranslatedBlock? FindReaderSegmentBlock(int start, int length, string selected)
    {
        var end = start + Math.Max(1, length);
        var best = _readerSegments
            .Select(segment =>
            {
                var overlap = Math.Max(0, Math.Min(end, segment.Start + segment.Length) - Math.Max(start, segment.Start));
                return (segment, overlap);
            })
            .Where(item => item.overlap > 0)
            .OrderByDescending(item => item.overlap)
            .Select(item => item.segment.Block)
            .FirstOrDefault();
        if (best != null)
            return best;

        var needle = NormalizeSelectionSearch(selected);
        if (needle.Length == 0)
            return null;

        return _drawable.Blocks.FirstOrDefault(block =>
            NormalizeSelectionSearch(block.TranslatedText).Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            NormalizeSelectionSearch(block.OriginalText).Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private RectF? ResolveSourceCanvasBounds(TranslatedBlock block)
    {
        if (_drawable.CaptureWidth <= 0 || _drawable.CaptureHeight <= 0 || Canvas.Width <= 0 || Canvas.Height <= 0)
            return null;

        var sx = (float)(Canvas.Width / _drawable.CaptureWidth);
        var sy = (float)(Canvas.Height / _drawable.CaptureHeight);
        var lines = block.SourceLines
            .Where(line => line.Width > 0 && line.Height > 0)
            .ToList();
        if (lines.Count == 0)
            return new RectF(block.X * sx, block.Y * sy, MathF.Max(1, block.Width * sx), MathF.Max(1, block.Height * sy));

        var left = lines.Min(line => line.X) * sx;
        var top = lines.Min(line => line.Y) * sy;
        var right = lines.Max(line => line.X + line.Width) * sx;
        var bottom = lines.Max(line => line.Y + line.Height) * sy;
        return new RectF(left, top, MathF.Max(1, right - left), MathF.Max(1, bottom - top));
    }

    private string BuildDictionaryProof(string selected, TranslatedBlock block, int start, int length)
    {
        var blocks = _drawable.Blocks.ToList();
        var blockIndex = Math.Max(0, blocks.IndexOf(block));
        var leaves = blocks.Count == 0
            ? new List<string> { HashHex("empty") }
            : blocks.Select(BlockLeafHash).ToList();
        var root = BuildMerkleRoot(leaves);
        var path = BuildMerklePath(leaves, blockIndex);
        var source = BlockSourceLabel(block);
        var warning = block.TranslationSource == TranslationSourceKind.SarmadGateway
            ? "موثق عبر سرمد: نعم"
            : "تحذير: هذه الكتلة ليست ترجمة سرمد خالصة؛ لا تعتمد ترجمة مختلطة كمسار معجمي نهائي.";

        return string.Join(Environment.NewLine, new[]
        {
            "مسار إثبات محلي Merkle-style:",
            warning,
            $"المصدر: {source}",
            $"نطاق التحديد: {start}..{start + length}",
            $"OCR box: x={block.X}, y={block.Y}, w={block.Width}, h={block.Height}",
            $"term_hash={ShortHash(selected)}",
            $"original_hash={ShortHash(block.OriginalText)}",
            $"translation_hash={ShortHash(block.TranslatedText)}",
            $"leaf_hash={ShortHash(leaves[Math.Clamp(blockIndex, 0, leaves.Count - 1)])}",
            $"document_root={ShortHash(root)}",
            $"path={path}"
        });
    }

    private static string BlockLeafHash(TranslatedBlock block)
        => HashHex(string.Join("|", new[]
        {
            block.X.ToString(CultureInfo.InvariantCulture),
            block.Y.ToString(CultureInfo.InvariantCulture),
            block.Width.ToString(CultureInfo.InvariantCulture),
            block.Height.ToString(CultureInfo.InvariantCulture),
            block.OriginalText ?? "",
            block.TranslatedText ?? "",
            block.TranslationSource.ToString()
        }));

    private static string BuildMerkleRoot(IReadOnlyList<string> leaves)
    {
        var level = leaves.Count == 0 ? new List<string> { HashHex("empty") } : leaves.ToList();
        while (level.Count > 1)
        {
            var next = new List<string>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : left;
                next.Add(HashHex(left + right));
            }
            level = next;
        }
        return level[0];
    }

    private static string BuildMerklePath(IReadOnlyList<string> leaves, int index)
    {
        if (leaves.Count == 0)
            return "root-only";

        var level = leaves.ToList();
        var cursor = Math.Clamp(index, 0, level.Count - 1);
        var parts = new List<string>();
        while (level.Count > 1)
        {
            var sibling = cursor % 2 == 0 ? cursor + 1 : cursor - 1;
            if (sibling >= level.Count)
                sibling = cursor;
            parts.Add($"{(cursor % 2 == 0 ? "R" : "L")}:{ShortHash(level[sibling])}");

            var next = new List<string>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : left;
                next.Add(HashHex(left + right));
            }
            cursor /= 2;
            level = next;
        }
        return parts.Count == 0 ? "root-only" : string.Join(" > ", parts);
    }

    private static string ShortHash(string value)
        => HashHex(value ?? "").Substring(0, 16);

    private static string HashHex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void SetReaderVisible(bool visible)
    {
        ReaderPanel.IsVisible = visible;
        BtnReader.Text = "فصل القارئ";
        ApplyReaderVisibility();
    }

    private void ApplyReaderVisibility()
    {
        var hasTranslation = !_liveMode && _drawable.Blocks.Count > 0;
        _drawable.ShowTranslations = false;
        NativeOverlayEditor.IsVisible = hasTranslation && !ReaderPanel.IsVisible;
        SetScrollButtonsEnabled(false);
        Canvas.Invalidate();
    }

    private void UpdateReaderPanel(MirrorResult result)
    {
        var rtl = IsRtl(_settings.Current.TargetLanguage);
        ReaderEditor.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ReaderEditor.HorizontalTextAlignment = TextAlignment.Start;
        ReaderEditor.FontSize = _readerFontSize;
        var text = BuildNativeReaderText(result.Blocks, rtl, out var segments);
        _readerSegments = segments;
        UpdateNativeOverlay(text, rtl);
        if (!string.Equals(_readerLastText, text, StringComparison.Ordinal))
        {
            ReaderEditor.Text = text;
            _readerLastText = text;
        }
        ReaderSourceDetails.Text = "الأصل المعرفي: " + LocalizeTranslationSourceDetail(result.TranslationSourceLabel);
    }

    private void UpdateNativeOverlay(IReadOnlyList<TranslatedBlock> blocks, bool rightToLeft)
        => UpdateNativeOverlay(BuildNativeReaderText(blocks, rightToLeft), rightToLeft);

    private void UpdateNativeOverlay(string text, bool rightToLeft)
    {
        NativeOverlayEditor.FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        NativeOverlayEditor.HorizontalTextAlignment = TextAlignment.Start;
        NativeOverlayEditor.FontSize = _readerFontSize;
        if (!string.Equals(_nativeOverlayLastText, text, StringComparison.Ordinal))
        {
            NativeOverlayEditor.Text = text;
            _nativeOverlayLastText = text;
        }
    }

    private static string BuildNativeReaderText(IReadOnlyList<TranslatedBlock> blocks, bool rightToLeft)
        => BuildNativeReaderText(blocks, rightToLeft, out _);

    private static string BuildNativeReaderText(IReadOnlyList<TranslatedBlock> blocks, bool rightToLeft, out List<ReaderTextSegment> segments)
    {
        var builtSegments = new List<ReaderTextSegment>();
        var ordered = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.TranslatedText))
            .OrderBy(block => block.Y)
            .ThenBy(block => block.X)
            .ToList();
        if (ordered.Count == 0)
        {
            segments = builtSegments;
            return "";
        }

        var heights = ordered
            .Select(block => block.LineHeightHint > 0 ? block.LineHeightHint : block.Height)
            .Where(height => height > 0)
            .OrderBy(height => height)
            .ToList();
        var medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 18;

        var sb = new StringBuilder();
        var paragraph = new StringBuilder();
        var paragraphSegments = new List<(TranslatedBlock Block, int Start, int Length)>();
        TranslatedBlock? previousBody = null;

        foreach (var block in ordered)
        {
            var text = PrepareReaderDisplayText(block.TranslatedText.Trim(), rightToLeft);
            if (text.Length == 0)
                continue;

            if (block.Font.Role is DocumentTextRole.Title or DocumentTextRole.Heading)
            {
                FlushParagraph();
                var start = sb.Length;
                sb.AppendLine(text);
                builtSegments.Add(new ReaderTextSegment(start, text.Length, block));
                sb.AppendLine();
                previousBody = null;
                continue;
            }

            if (block.Font.Role == DocumentTextRole.Caption)
            {
                FlushParagraph();
                var start = sb.Length;
                sb.AppendLine(text);
                builtSegments.Add(new ReaderTextSegment(start, text.Length, block));
                sb.AppendLine();
                previousBody = null;
                continue;
            }

            var sameParagraph = previousBody != null &&
                block.Y - (previousBody.Y + previousBody.Height) <= medianHeight * 1.9 &&
                Math.Abs(block.X - previousBody.X) <= medianHeight * 3.0;

            if (!sameParagraph)
                FlushParagraph();

            if (paragraph.Length > 0)
                paragraph.Append(' ');
            var paragraphStart = paragraph.Length;
            paragraph.Append(text);
            paragraphSegments.Add((block, paragraphStart, text.Length));
            previousBody = block;
        }

        FlushParagraph();
        var result = sb.ToString().Trim();
        segments = builtSegments
            .Where(segment => segment.Start < result.Length)
            .Select(segment => segment with { Length = Math.Min(segment.Length, result.Length - segment.Start) })
            .Where(segment => segment.Length > 0)
            .ToList();
        return result;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
                return;

            var baseStart = sb.Length;
            sb.AppendLine(paragraph.ToString());
            foreach (var segment in paragraphSegments)
                builtSegments.Add(new ReaderTextSegment(baseStart + segment.Start, segment.Length, segment.Block));
            sb.AppendLine();
            paragraph.Clear();
            paragraphSegments.Clear();
        }
    }

    private static string PrepareReaderDisplayText(string text, bool rightToLeft)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var clean = StripVisibleBidiControls(text);
        return Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private static string StripVisibleBidiControls(string text)
        => string.IsNullOrEmpty(text)
            ? ""
            : Regex.Replace(text, "[\u200E\u200F\u202A-\u202E\u2066-\u2069]", "");

    private static string LocalizeTranslationSourceDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "غير معروف";

        return detail
            .Replace("mixed sources:", "مختلط لأن", StringComparison.OrdinalIgnoreCase)
            .Replace("Sarmad AI", "سرمد AI", StringComparison.OrdinalIgnoreCase)
            .Replace("Sarmad=", "سرمد=", StringComparison.OrdinalIgnoreCase)
            .Replace("MT fallback (non-academic)", "MT غير أكاديمي", StringComparison.OrdinalIgnoreCase)
            .Replace("MT=", "MT=", StringComparison.OrdinalIgnoreCase)
            .Replace("Original=", "الأصل=", StringComparison.OrdinalIgnoreCase)
            .Replace("original fallback", "النص الأصلي", StringComparison.OrdinalIgnoreCase);
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
        if (_busy || (_dragging && !_wheelResizing) || DictionaryPanel.IsVisible || _selectedDictionaryBlock != null)
            return;

        if (sender is not Microsoft.UI.Xaml.UIElement element) return;
        var delta = e.GetCurrentPoint(element).Properties.MouseWheelDelta;
        if (delta == 0) return;

        if (_drawable.ShowTranslations && !IsControlWheel(e))
        {
            AdjustContentScroll(delta > 0 ? -120 : 120);
        }
        else
        {
            var notches = Math.Clamp(delta / 120.0, -6.0, 6.0);
            BeginWheelResize();
            ResizeWindowAroundCenter(Math.Pow(WheelResizeStep, notches), GetRegion());
            ScheduleWheelResizeEnd();
        }

        e.Handled = true;
    }

    private static bool IsControlWheel(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);

    private void OnCanvasPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_busy || _dragging || !_drawable.ShowTranslations || _drawable.Blocks.Count == 0)
            return;
        if (sender is not Microsoft.UI.Xaml.UIElement element) return;

        var point = e.GetCurrentPoint(element);
        var canvasX = point.Position.X;
        var canvasY = point.Position.Y;
        var pointerKind = point.Properties.PointerUpdateKind.ToString();
        var explicitSelection =
            point.Properties.IsRightButtonPressed ||
            pointerKind.Contains("RightButtonPressed", StringComparison.OrdinalIgnoreCase) ||
            DictionaryPanel.IsVisible;
        var primarySelection =
            point.Properties.IsLeftButtonPressed ||
            pointerKind.Contains("LeftButtonPressed", StringComparison.OrdinalIgnoreCase);

        var hit = FindHitRegionAtCanvasPoint(canvasX, canvasY);
        if (hit == null)
        {
            if (explicitSelection)
                e.Handled = true;
            return;
        }
        if (!explicitSelection && !primarySelection)
            return;

        SelectDictionaryHit(hit, DictionarySelectionScope.Word, new PointF((float)canvasX, (float)canvasY));
        DictionaryPanel.IsVisible = true;
        ShowDictionaryStatus("انقر «معجم» لتحليل الكلمة، أو «جملة» لتوسيع الاختيار إلى سياق كامل قبل التحليل.");
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

        if (nearestDistance <= 32 * 32)
            return nearest;

        return null;
    }

    private static bool Contains(RectF rect, double x, double y)
        => x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;

    private static bool ContainsInflated(RectF rect, double x, double y, double padding)
        => x >= rect.Left - padding && x <= rect.Right + padding &&
           y >= rect.Top - padding && y <= rect.Bottom + padding;

    private void SelectDictionaryHit(MirrorDrawable.TextHitRegion hit, DictionarySelectionScope scope, PointF? clickPoint = null)
    {
        var selectedHit = scope == DictionarySelectionScope.Sentence
            ? ExpandToSentenceHit(hit)
            : hit;
        _lastDictionaryHit = selectedHit;
        _selectedDictionaryBlock = selectedHit.Block;
        var fallback = !string.IsNullOrWhiteSpace(selectedHit.Block.TranslatedText)
            ? selectedHit.Block.TranslatedText
            : selectedHit.Block.OriginalText;
        _selectedDictionaryText = string.IsNullOrWhiteSpace(selectedHit.Text)
            ? fallback.Trim()
            : selectedHit.Text.Trim();
        _selectedDictionaryProof = BuildDictionaryProof(_selectedDictionaryText, selectedHit.Block, 0, _selectedDictionaryText.Length);
        _drawable.SelectedBlock = selectedHit.Block;
        _drawable.SelectedText = _selectedDictionaryText;
        _drawable.SelectedTextBounds = ResolveSelectionHighlightBounds(selectedHit, clickPoint);
        _drawable.SelectedTextIsSource = selectedHit.IsSource;
        _drawable.SelectedSourceTextBounds = ResolveCounterpartSourceBounds(selectedHit) ?? ResolveSourceCanvasBounds(selectedHit.Block);
        var label = scope == DictionarySelectionScope.Sentence || selectedHit.IsLine
            ? "جملة"
            : selectedHit.IsWord ? "كلمة" : "نص";
        BtnDictionarySentence.Text = label == "جملة" ? "جملة ✓" : "جملة";
        LblDictionarySelection.Text = $"{label}: {Trunc(_selectedDictionaryText, 72)} · مصدر: {BlockSourceLabel(selectedHit.Block)}";
    }

    private static string BlockSourceLabel(TranslatedBlock block) => block.TranslationSource switch
    {
        TranslationSourceKind.SarmadGateway => "سرمد",
        TranslationSourceKind.MachineTranslationFallback => "MT غير أكاديمي",
        TranslationSourceKind.OriginalTextFallback => "الأصل",
        TranslationSourceKind.Mixed => "مختلط",
        _ => string.IsNullOrWhiteSpace(block.TranslationSourceLabel) ? "غير معروف" : block.TranslationSourceLabel,
    };

    private static RectF? ResolveSelectionHighlightBounds(MirrorDrawable.TextHitRegion hit, PointF? clickPoint)
    {
        if (hit.IsSource)
            return hit.Bounds;

        return hit.IsWord || hit.Bounds.Height <= 120 ? hit.Bounds : null;
    }

    private RectF? ResolveCounterpartSourceBounds(MirrorDrawable.TextHitRegion selectedHit)
    {
        if (selectedHit.IsSource)
            return null;

        var sourceLines = _drawable.HitRegions
            .Where(h => ReferenceEquals(h.Block, selectedHit.Block) && h.IsSource && h.Scope == MirrorDrawable.TextHitScope.Line)
            .OrderBy(h => h.LineIndex)
            .ToList();
        if (sourceLines.Count > 0)
        {
            var translatedLine = selectedHit.IsLine ? selectedHit : FindLineRegionForHit(selectedHit);
            if (translatedLine?.LineIndex >= 0)
            {
                var translatedLineCount = _drawable.HitRegions.Count(h =>
                    ReferenceEquals(h.Block, selectedHit.Block) &&
                    !h.IsSource &&
                    h.Scope == MirrorDrawable.TextHitScope.Line);
                var sourceIndex = MapTranslatedLineToSourceLine(translatedLine.LineIndex, translatedLineCount, sourceLines.Count);
                return sourceLines.FirstOrDefault(h => h.LineIndex == sourceIndex)?.Bounds ??
                       sourceLines[Math.Clamp(sourceIndex, 0, sourceLines.Count - 1)].Bounds;
            }

            var selectedCenterY = selectedHit.Bounds.Top + selectedHit.Bounds.Height / 2.0;
            return sourceLines
                .OrderBy(h => Math.Abs((h.Bounds.Top + h.Bounds.Height / 2.0) - selectedCenterY))
                .First()
                .Bounds;
        }

        return _drawable.HitRegions
            .FirstOrDefault(h => ReferenceEquals(h.Block, selectedHit.Block) && h.IsSource && h.Scope == MirrorDrawable.TextHitScope.Source)
            ?.Bounds;
    }

    private static int MapTranslatedLineToSourceLine(int translatedLineIndex, int translatedLineCount, int sourceLineCount)
    {
        if (sourceLineCount <= 1 || translatedLineCount <= 1)
            return 0;

        var ratio = Math.Clamp(translatedLineIndex / (double)(translatedLineCount - 1), 0.0, 1.0);
        return Math.Clamp((int)Math.Round(ratio * (sourceLineCount - 1)), 0, sourceLineCount - 1);
    }

    private void OnDictionarySelectSentence(object? sender, EventArgs e)
    {
        if (_lastDictionaryHit == null)
        {
            ShowDictionaryStatus("انقر أولًا على كلمة داخل النص المترجم، ثم استخدم «جملة» لتوسيع الاختيار.");
            return;
        }

        SelectDictionaryHit(_lastDictionaryHit, DictionarySelectionScope.Sentence);
        ShowDictionaryStatus("تم اختيار الجملة المحيطة بالكلمة. انقر «معجم» لتحليلها مع السياق.");
        SetStatus($"جملة للمعجم: {Trunc(_selectedDictionaryText, 38)}");
        Canvas.Invalidate();
    }

    private MirrorDrawable.TextHitRegion ExpandToSentenceHit(MirrorDrawable.TextHitRegion hit)
    {
        var lineHit = FindLineRegionForHit(hit);
        var source = hit.IsSource
            ? hit.Block.OriginalText
            : string.IsNullOrWhiteSpace(hit.Block.TranslatedText) ? hit.Block.OriginalText : hit.Block.TranslatedText;
        var sentence = ExtractSentenceContainingText(source, hit.Text);
        if (string.IsNullOrWhiteSpace(sentence) && lineHit != null)
            sentence = lineHit.Text;
        if (string.IsNullOrWhiteSpace(sentence))
            sentence = hit.Text;

        return hit with
        {
            Text = LimitSelectionText(sentence),
            Bounds = lineHit?.Bounds ?? hit.Bounds,
            IsWord = false,
            IsSource = hit.IsSource,
            Scope = MirrorDrawable.TextHitScope.Line
        };
    }

    private MirrorDrawable.TextHitRegion? FindLineRegionForHit(MirrorDrawable.TextHitRegion hit)
    {
        if (hit.IsLine)
            return hit;

        var centerX = hit.Bounds.Left + hit.Bounds.Width / 2.0;
        var centerY = hit.Bounds.Top + hit.Bounds.Height / 2.0;
        MirrorDrawable.TextHitRegion? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (var candidate in _drawable.HitRegions.Where(h => ReferenceEquals(h.Block, hit.Block) && h.IsLine && h.IsSource == hit.IsSource))
        {
            if (ContainsInflated(candidate.Bounds, centerX, centerY, 6))
                return candidate;

            var candidateCenterY = candidate.Bounds.Top + candidate.Bounds.Height / 2.0;
            var distance = Math.Abs(candidateCenterY - centerY);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }

        return nearestDistance <= Math.Max(24, hit.Bounds.Height * 1.4) ? nearest : null;
    }

    private static string ExtractSentenceContainingText(string text, string selected)
    {
        var normalized = Regex.Replace((text ?? "").Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
        if (normalized.Length == 0)
            return "";

        var needle = NormalizeSelectionSearch(selected);
        foreach (Match match in Regex.Matches(normalized, @"[^.!?؟؛]+[.!?؟؛]*"))
        {
            var sentence = match.Value.Trim();
            if (sentence.Length == 0)
                continue;

            if (needle.Length == 0 || NormalizeSelectionSearch(sentence).Contains(needle, StringComparison.OrdinalIgnoreCase))
                return sentence;
        }

        return normalized.Length <= 420 ? normalized : selected;
    }

    private static string NormalizeSelectionSearch(string text)
        => Regex.Replace(text ?? "", @"[\p{P}\p{S}\s]+", " ").Trim();

    private static string LimitSelectionText(string text)
    {
        var clean = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        return clean.Length <= 420 ? clean : clean.Substring(0, 419).TrimEnd() + "…";
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
            ShowDictionaryStatus("تعذر تنفيذ التحليل المعجمي. راجع سجل المرآة لمعرفة سبب بوابة سرمد.");
        }
        finally
        {
            _busy = false;
            BtnDictionaryExplain.IsEnabled = true;
        }
        await DictionaryScroll.ScrollToAsync(0, 0, false);
    }

    private void OnDictionaryApplyAlternative(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string proposal } ||
            string.IsNullOrWhiteSpace(proposal))
        {
            ShowDictionaryStatus("لم أستطع قراءة البديل المراد اعتماده من بطاقة المعجم.");
            return;
        }
        if (_selectedDictionaryBlock == null || string.IsNullOrWhiteSpace(_selectedDictionaryText))
        {
            ShowDictionaryStatus("اختر كلمة أو جملة من النص قبل اعتماد بديل من المعجم.");
            return;
        }

        var replacement = CleanAcceptedAlternative(proposal);
        var previousSelection = _selectedDictionaryText;
        var previousTranslation = _selectedDictionaryBlock.TranslatedText;
        var previousTypewriterTarget = GetTypewriterTargetOrVisible(_selectedDictionaryBlock);
        var contextBeforeUpdate = BuildDictionaryContext();
        if (!TryReplaceSelectedTranslation(previousTranslation, previousSelection, replacement, out var updatedTranslation))
        {
            ShowDictionaryStatus("تعذر تطبيق البديل بأمان لأن النص المحدد لم يعد موجودًا في الترجمة المعروضة. اختر الكلمة مرة أخرى ثم اعتمد البديل.");
            return;
        }

        var updatedTypewriterTarget = TryReplaceSelectedTranslation(
            previousTypewriterTarget, previousSelection, replacement, out var targetCandidate)
            ? targetCandidate
            : updatedTranslation;

        try
        {
            SetVisibleTranslationAndTypewriterTarget(_selectedDictionaryBlock, updatedTranslation, updatedTypewriterTarget);
            var rule = _glossaryMemory.RememberSelection(
                _selectedDictionaryBlock.OriginalText,
                previousSelection,
                replacement,
                _settings.Current.TargetLanguage,
                contextBeforeUpdate);

            _selectedDictionaryText = replacement;
            _drawable.SelectedText = replacement;
            _drawable.SelectedTextBounds = null;
            _drawable.SelectedTextIsSource = false;
            _drawable.SelectedSourceTextBounds = null;
            SetSelectionOverlay(null, null);
            _lastDictionaryHit = _lastDictionaryHit == null ? null : _lastDictionaryHit with { Text = replacement, IsWord = false };
            LblDictionarySelection.Text = $"اعتمد: {Trunc(replacement, 72)}";
            BtnDictionarySentence.Text = "جملة";
            Canvas.Invalidate();
            SetStatus($"✓ حُفظت قاعدة مصطلحية ({rule.UseCount}) وتحدثت الترجمة");
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Apply dictionary alternative", ex);
            ShowDictionaryStatus("تعذر حفظ قاعدة المصطلح. بقيت الترجمة دون تغيير.");
            SetVisibleTranslationAndTypewriterTarget(_selectedDictionaryBlock, previousTranslation, previousTypewriterTarget);
            Canvas.Invalidate();
        }
    }

    private void OnDictionaryClose(object? sender, EventArgs e)
    {
        DictionaryPanel.IsVisible = false;
        _selectedDictionaryBlock = null;
        _lastDictionaryHit = null;
        _selectedDictionaryText = "";
        _selectedDictionaryProof = "";
        _drawable.SelectedBlock = null;
        _drawable.SelectedText = null;
        _drawable.SelectedTextBounds = null;
        _drawable.SelectedTextIsSource = false;
        _drawable.SelectedSourceTextBounds = null;
        SetSelectionOverlay(null, null);
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

        var context = string.Join(
            Environment.NewLine,
            Enumerable.Range(start, Math.Max(0, end - start + 1)).Select(i =>
            {
                var block = blocks[i];
                return $"{i + 1}. O: {CompactContextLine(block.OriginalText)} | T: {CompactContextLine(block.TranslatedText)}";
            }));
        if (!string.IsNullOrWhiteSpace(_selectedDictionaryProof))
        {
            context =
                "PROOF PATH / مسار الإثبات:\n" +
                _selectedDictionaryProof + "\n\n" +
                "AUDITOR INSTRUCTION / أمر المدقق: لا تنتج ترجمة مختلطة. اربط المدخل المعجمي بالأصل OCR وبالترجمة السرمدية إن كانت موثقة؛ إذا كان المصدر MT أو الأصل فقط فصرح أنه غير موثق كمسار سرمد.\n\n" +
                context;
        }
        return context;
    }

    private static string CompactContextLine(string text)
    {
        var line = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 120 ? line : line.Substring(0, 119).TrimEnd() + "…";
    }

    private void ShowDictionaryStatus(string message)
    {
        DictionaryResultStack.Children.Clear();
        DictionaryProofStack.Children.Clear();
        DictionaryResultStack.FlowDirection = DictionaryFlowDirection();
        DictionaryProofStack.FlowDirection = FlowDirection.LeftToRight;
        AddDictionarySection(DictionaryResultStack, "حالة المعجم", new[] { message }, "#14243A", "#3357E3FF", technicalSection: false);
        ShowDictionaryProof();
        SetDictionaryTab(showProof: false);
    }

    private void ShowDictionaryResult(string result)
    {
        DictionaryResultStack.Children.Clear();
        DictionaryProofStack.Children.Clear();
        DictionaryResultStack.FlowDirection = DictionaryFlowDirection();
        DictionaryProofStack.FlowDirection = FlowDirection.LeftToRight;
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
                DictionaryResultStack,
                title,
                lines,
                warning ? "#2A1E22" : "#14243A",
                warning ? "#66FFB347" : "#3357E3FF",
                technicalSection: false);
        }
        ShowDictionaryProof();
        SetDictionaryTab(showProof: false);
    }

    private void ShowDictionaryProof()
    {
        DictionaryProofStack.Children.Clear();
        DictionaryProofStack.FlowDirection = FlowDirection.LeftToRight;
        if (string.IsNullOrWhiteSpace(_selectedDictionaryProof))
        {
            AddDictionarySection(
                DictionaryProofStack,
                "Technical proof",
                new[]
                {
                    "No selected OCR/Merkle proof is available for this dictionary request.",
                    "Select text from the reader or mirror editor before opening the dictionary."
                },
                "#182033",
                "#668B5CF6",
                technicalSection: true);
            return;
        }

        AddDictionarySection(
            DictionaryProofStack,
            "مسار الإثبات التقني",
            _selectedDictionaryProof.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            "#182033",
            "#668B5CF6",
            technicalSection: true);
    }

    private void OnDictionaryTabLexical(object? sender, EventArgs e) => SetDictionaryTab(showProof: false);

    private void OnDictionaryTabProof(object? sender, EventArgs e) => SetDictionaryTab(showProof: true);

    private void SetDictionaryTab(bool showProof)
    {
        DictionaryScroll.IsVisible = !showProof;
        DictionaryProofScroll.IsVisible = showProof;
        BtnDictionaryTabLexical.BackgroundColor = Color.FromArgb(showProof ? "#10243A" : "#1E5C82");
        BtnDictionaryTabLexical.TextColor = Color.FromArgb(showProof ? "#B7D7EF" : "#FFFFFF");
        BtnDictionaryTabProof.BackgroundColor = Color.FromArgb(showProof ? "#1E5C82" : "#10243A");
        BtnDictionaryTabProof.TextColor = Color.FromArgb(showProof ? "#FFFFFF" : "#B7D7EF");
    }

    private static string NormalizeDictionaryResult(string result)
    {
        var raw = (result ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (raw.Length == 0)
            return "";

        var text = string.Join(
            "\n",
            raw.Split('\n')
                .SelectMany(ConvertDictionaryMarkdownLine)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

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
        line = CleanDictionaryMarkdown(line);
        var standalone = line.Trim().TrimEnd(':', '：');
        if (DictionaryHeadings.Any(h => standalone.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
        {
            title = standalone;
            return true;
        }

        var colon = line.IndexOf(':');
        if (colon < 0)
            colon = line.IndexOf('：');
        if (colon <= 0)
            return false;

        var candidate = line[..colon].Trim();
        if (!DictionaryHeadings.Any(h => candidate.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
            return false;

        title = candidate;
        inline = line[(colon + 1)..].Trim();
        return true;
    }

    private void AddDictionarySection(
        VerticalStackLayout targetStack,
        string title,
        IEnumerable<string> rawLines,
        string background,
        string stroke,
        bool technicalSection)
    {
        var fallbackRtl = DictionaryIsRtl();
        var sectionRtl = technicalSection ? false : fallbackRtl;
        var flow = FlowForDirection(sectionRtl);
        var alignment = AlignmentForDirection(sectionRtl);
        var card = new Border
        {
            StrokeThickness = 1,
            Padding = new Thickness(12, 10),
            BackgroundColor = Color.FromArgb(background),
            Stroke = Color.FromArgb(stroke)
        };
        card.StrokeShape = new RoundRectangle { CornerRadius = 10 };
        var stack = new VerticalStackLayout
        {
            Spacing = 9,
            FlowDirection = flow
        };
        stack.Children.Add(new Label
        {
            Text = PrepareDictionaryDisplayText(title, sectionRtl),
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#9DEBFF"),
            HorizontalTextAlignment = alignment,
            LineBreakMode = LineBreakMode.WordWrap,
            LineHeight = 1.45,
            FlowDirection = flow
        });

        foreach (var raw in rawLines.SelectMany(SplitLongDictionaryLine))
        {
            var line = CleanDictionaryMarkdown(raw);
            if (line.Length == 0)
                continue;

            var lineRtl = technicalSection ? false : fallbackRtl;
            var lineFlow = FlowForDirection(lineRtl);
            var lineAlignment = AlignmentForDirection(lineRtl);
            var technical = technicalSection || IsMostlyTechnicalDictionaryLine(line) && !HasRtlStrong(line);
            if (TrySplitDictionaryKeyValue(line, out var key, out var value))
            {
                stack.Children.Add(CreateDictionaryKeyValueRow(key, value, fallbackRtl, technicalSection));
                continue;
            }

            var textLabel = new Label
            {
                Text = technical ? BreakLongTechnicalRuns(line) : PrepareDictionaryDisplayText(line, lineRtl),
                FontSize = LooksLikeAlternativeLine(line) ? 13.4 : 13.0,
                TextColor = Color.FromArgb(technical ? "#E8F6FF" : "#CFEFFF"),
                FlowDirection = technical ? FlowDirection.LeftToRight : lineFlow,
                HorizontalTextAlignment = technical ? TextAlignment.Start : lineAlignment,
                LineBreakMode = LineBreakMode.WordWrap,
                LineHeight = 1.55,
            };

            if (LooksLikeAlternativeLine(line))
            {
                var altStack = new VerticalStackLayout
                {
                    Spacing = 7,
                    FlowDirection = technical ? FlowDirection.LeftToRight : lineFlow
                };
                altStack.Children.Add(textLabel);
                if (TryExtractDictionaryAlternative(line, out var proposal))
                {
                    var applyButton = new Button
                    {
                        Text = $"اعتمد: {Trunc(proposal, 24)}",
                        CommandParameter = proposal,
                        FontSize = 12,
                        Padding = new Thickness(10, 0),
                        HeightRequest = 32,
                        CornerRadius = 8,
                        BackgroundColor = Color.FromArgb("#245B3A"),
                        TextColor = Color.FromArgb("#E5FFE8"),
                        BorderColor = Color.FromArgb("#5586EFAC"),
                        BorderWidth = 1,
                        FlowDirection = FlowDirection.RightToLeft
                    };
                    applyButton.Clicked += OnDictionaryApplyAlternative;
                    altStack.Children.Add(applyButton);
                }

                var alt = new Border
                {
                    Padding = new Thickness(10, 7),
                    BackgroundColor = Color.FromArgb("#1D3552"),
                    Stroke = Color.FromArgb("#244DD0E1"),
                    StrokeThickness = 1,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Content = altStack
                };
                stack.Children.Add(alt);
            }
            else
            {
                stack.Children.Add(textLabel);
            }
        }

        card.Content = stack;
        targetStack.Children.Add(card);
    }

    private static IEnumerable<string> SplitLongDictionaryLine(string line)
    {
        var normalized = CleanDictionaryMarkdown(line);
        if (normalized.Length == 0)
            yield break;

        foreach (var part in Regex.Split(normalized, @"(?=\b[1-9][\.\)]\s+)"))
        {
            var item = part.Trim();
            if (item.Length > 0)
                yield return item;
        }
    }

    private static IEnumerable<string> ConvertDictionaryMarkdownLine(string line)
    {
        var clean = CleanDictionaryMarkdown(line);
        if (clean.Length == 0 || clean == "---")
            yield break;

        if (clean.StartsWith("|") && clean.EndsWith("|"))
        {
            foreach (var converted in ConvertMarkdownTableRow(clean))
                yield return converted;
            yield break;
        }

        yield return clean;
    }

    private static IEnumerable<string> ConvertMarkdownTableRow(string line)
    {
        var cells = line.Trim().Trim('|')
            .Split('|')
            .Select(CleanDictionaryMarkdown)
            .Where(cell => cell.Length > 0)
            .ToArray();

        if (cells.Length == 0 || cells.All(cell => cell.All(ch => ch == '-' || ch == ':' || char.IsWhiteSpace(ch))))
            yield break;

        if (IsAny(cells[0], "#", "رقم") ||
            (cells.Length >= 2 && IsAny(cells[0], "العنصر", "Item") && IsAny(cells[1], "الوصف", "Description")) ||
            (cells.Length >= 2 && IsAny(cells[0], "بديل", "Alternative") && IsAny(cells[1], "ملاءمة", "Fit")))
            yield break;

        if (cells.Length >= 4 && int.TryParse(cells[0], out _))
        {
            yield return $"{cells[0]}. {cells[1]} — {cells[2]} — {cells[3]}";
            yield break;
        }

        if (cells.Length >= 2)
        {
            yield return $"{cells[0]}: {string.Join(" — ", cells.Skip(1))}";
            yield break;
        }

        yield return string.Join(" — ", cells);
    }

    private static bool TryExtractDictionaryAlternative(string line, out string proposal)
    {
        proposal = "";
        var clean = CleanDictionaryMarkdown(line);
        clean = Regex.Replace(clean, @"^\s*(?:[-•]\s*)?[1-9][\.\)]\s*", "").Trim();
        if (clean.Length == 0)
            return false;

        clean = Regex.Replace(clean, @"^(?:البديل|بديل|المقترح|اقتراح|Alternative)\s*[:：-]?\s*", "", RegexOptions.IgnoreCase).Trim();
        var separators = new[] { " — ", " – ", " - ", "؛", "،", ":", "：" };
        foreach (var separator in separators)
        {
            var index = clean.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                clean = clean[..index].Trim();
                break;
            }
        }

        proposal = CleanAcceptedAlternative(clean);
        return proposal.Length > 0 && proposal.Length <= 96;
    }

    private static string CleanAcceptedAlternative(string text)
    {
        var clean = CleanDictionaryMarkdown(text);
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        clean = clean.Trim(' ', '.', ',', '،', ':', ';', '؛', '(', ')', '[', ']', '"', '\'', '«', '»');
        return clean.Length <= 96 ? clean : clean.Substring(0, 95).TrimEnd() + "…";
    }

    private static bool TryReplaceSelectedTranslation(string currentTranslation, string selectedText, string replacement, out string updated)
    {
        updated = currentTranslation;
        if (string.IsNullOrWhiteSpace(currentTranslation) ||
            string.IsNullOrWhiteSpace(selectedText) ||
            string.IsNullOrWhiteSpace(replacement))
            return false;

        if (TryReplaceFirst(currentTranslation, selectedText.Trim(), replacement, out updated))
            return true;

        var cleanSelected = CleanAcceptedAlternative(selectedText);
        if (!string.Equals(cleanSelected, selectedText, StringComparison.Ordinal) &&
            TryReplaceFirst(currentTranslation, cleanSelected, replacement, out updated))
            return true;

        var normalizedCurrent = NormalizeSelectionSearch(currentTranslation);
        var normalizedSelected = NormalizeSelectionSearch(selectedText);
        if (normalizedSelected.Length == 0 || !normalizedCurrent.Contains(normalizedSelected, StringComparison.OrdinalIgnoreCase))
            return false;

        var tokens = currentTranslation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = CleanAcceptedAlternative(tokens[i]);
            if (!string.Equals(token, cleanSelected, StringComparison.OrdinalIgnoreCase))
                continue;

            tokens[i] = tokens[i].Replace(token, replacement, StringComparison.OrdinalIgnoreCase);
            updated = string.Join(' ', tokens);
            return true;
        }

        return false;
    }

    private static bool TryReplaceFirst(string text, string selected, string replacement, out string updated)
    {
        updated = text;
        if (selected.Length == 0)
            return false;

        var index = text.IndexOf(selected, StringComparison.Ordinal);
        if (index < 0)
            index = text.IndexOf(selected, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;

        updated = text[..index] + replacement + text[(index + selected.Length)..];
        return true;
    }

    private static bool IsAny(string value, params string[] candidates)
        => candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static string CleanDictionaryMarkdown(string text)
    {
        var clean = (text ?? "").Trim();
        clean = Regex.Replace(clean, @"^#{1,6}\s*", "");
        clean = clean.Replace("**", "").Replace("__", "").Replace("`", "");
        clean = Regex.Replace(clean, @"^\s*[-*]\s+", "• ");
        return clean.Trim();
    }

    private View CreateDictionaryKeyValueRow(string key, string value, bool fallbackRtl, bool technicalSection)
    {
        var keyRtl = technicalSection ? false : fallbackRtl;
        var valueRtl = technicalSection ? false : fallbackRtl;
        var technical = technicalSection || IsMostlyTechnicalDictionaryLine(value) && !HasRtlStrong(value);
        var stack = new VerticalStackLayout
        {
            Spacing = 5,
            FlowDirection = FlowForDirection(keyRtl)
        };
        stack.Children.Add(new Label
        {
            Text = PrepareDictionaryDisplayText(key, keyRtl),
            FontSize = 12.2,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#FFD166"),
            HorizontalTextAlignment = AlignmentForDirection(keyRtl),
            LineBreakMode = LineBreakMode.WordWrap,
            FlowDirection = FlowForDirection(keyRtl),
            LineHeight = 1.35
        });

        stack.Children.Add(new Label
        {
            Text = technical ? BreakLongTechnicalRuns(value) : PrepareDictionaryDisplayText(value, valueRtl),
            FontSize = 13.2,
            TextColor = Color.FromArgb("#DDF7FF"),
            HorizontalTextAlignment = technical ? TextAlignment.Start : AlignmentForDirection(valueRtl),
            LineBreakMode = LineBreakMode.WordWrap,
            LineHeight = 1.55,
            FlowDirection = technical ? FlowDirection.LeftToRight : FlowForDirection(valueRtl)
        });

        return new Border
        {
            Padding = new Thickness(9, 7),
            BackgroundColor = Color.FromArgb("#10243A"),
            Stroke = Color.FromArgb("#183A5A"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = stack
        };
    }

    private bool DictionaryIsRtl() => IsRtl(_settings.Current.TargetLanguage);

    private FlowDirection DictionaryFlowDirection()
        => DictionaryIsRtl() ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private static string PrepareDictionaryDisplayText(string text, bool rightToLeft)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        return StripVisibleBidiControls(text);
    }

    private static FlowDirection FlowForDirection(bool rightToLeft)
        => rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private static TextAlignment AlignmentForDirection(bool rightToLeft)
        => rightToLeft ? TextAlignment.End : TextAlignment.Start;

    private static bool DictionaryTextIsRtl(string text, bool fallbackRtl)
    {
        foreach (var ch in text ?? "")
        {
            if (IsStrongRtl(ch))
                return true;
            if (IsStrongLtr(ch))
                return false;
        }

        return fallbackRtl;
    }

    private static bool HasRtlStrong(string text)
        => (text ?? "").Any(IsStrongRtl);

    private static bool IsStrongRtl(char ch)
        => ch is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';

    private static bool IsStrongLtr(char ch)
        => char.IsLetter(ch) && !IsStrongRtl(ch);

    private static string BreakLongTechnicalRuns(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        return Regex.Replace(
            text,
            @"[A-Za-z0-9][A-Za-z0-9._:/@#%+\-=()]{16,}",
            m => Regex.Replace(m.Value, @"([._:/@#%+\-=()])", "$1\u200B"));
    }

    private static bool TrySplitDictionaryKeyValue(string line, out string key, out string value)
    {
        key = "";
        value = "";
        var colon = line.IndexOf(':');
        if (colon < 0)
            colon = line.IndexOf('：');
        if (colon <= 0 || colon > 42)
            return false;

        key = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        return key.Length > 0 && value.Length > 0;
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

    private void OnSmaller(object? sender, EventArgs e) => AdjustNativeTextFont(-1);
    private void OnBigger(object? sender, EventArgs e) => AdjustNativeTextFont(+1);

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

    private void AdjustNativeTextFont(double delta)
    {
        var next = Math.Clamp(_readerFontSize + delta, 12, 28);
        if (Math.Abs(next - _readerFontSize) < 0.001)
        {
            SetStatus(delta < 0 ? "🔠 أصغر حجم" : "🔠 أكبر حجم");
            return;
        }

        _readerFontSize = next;
        ReaderEditor.FontSize = _readerFontSize;
        NativeOverlayEditor.FontSize = _readerFontSize;
        SetStatus($"خط النص {_readerFontSize:0}");
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
        ApplyReadingSurfaceAppearance(s);
        UpdateAppearanceButtons(s);
    }

    private void ApplyReadingSurfaceAppearance(MirrorSettings s)
    {
        var bg = MirrorAppearanceColors.ToColor(s.TranslationBackgroundColor, MirrorAppearanceColors.DefaultBackgroundHex);
        var ink = MirrorAppearanceColors.ToColor(s.TranslationTextColor, MirrorAppearanceColors.DefaultTextHex);
        var opacity = (float)Math.Clamp(s.TranslationBackgroundOpacity, 0, 1);
        var overlaySurface = bg.WithAlpha(opacity);
        var panelSurface = bg.WithAlpha((float)Math.Clamp(s.TranslationBackgroundOpacity, 0.65, 1));

        NativeOverlayEditor.TextColor = ink;
        NativeOverlayEditor.BackgroundColor = overlaySurface;
        NativeOverlayEditor.FontSize = _readerFontSize;

        ReaderEditor.TextColor = ink;
        ReaderEditor.BackgroundColor = panelSurface;
        ReaderEditor.FontSize = _readerFontSize;
        ReaderPanel.Background = new SolidColorBrush(panelSurface);

        DictionaryPanel.Background = new SolidColorBrush(panelSurface);
    }

    private static Button CreateReaderControlButton(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Padding = new Thickness(8, 0),
        HeightRequest = 32,
        CornerRadius = 8,
        BorderWidth = 1
    };

    private static void SetDetachedReaderWrap(Editor editor, bool wrap)
    {
#if WINDOWS
        if (editor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
        {
            textBox.TextWrapping = wrap
                ? Microsoft.UI.Xaml.TextWrapping.Wrap
                : Microsoft.UI.Xaml.TextWrapping.NoWrap;
            Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(
                textBox,
                wrap
                    ? Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled
                    : Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto);
        }
#endif
    }

    private void ConfigureEditorContextMenu(Editor editor, string surface)
    {
#if WINDOWS
        if (editor.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.TextBox textBox)
            return;

        var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();
        var dictionaryItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "معجم المحدد" };
        dictionaryItem.Click += (_, _) => SelectDictionaryFromEditor(editor, surface);

        var copyItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "نسخ" };
        copyItem.Click += async (_, _) =>
        {
            var selected = textBox.SelectedText;
            var text = string.IsNullOrEmpty(selected) ? textBox.Text : selected;
            if (!string.IsNullOrEmpty(text))
            {
                await Clipboard.Default.SetTextAsync(text);
                SetStatus("⧉ تم النسخ");
            }
        };

        var selectAllItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "تحديد الكل" };
        selectAllItem.Click += (_, _) => textBox.SelectAll();

        flyout.Items.Add(dictionaryItem);
        flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        flyout.Items.Add(copyItem);
        flyout.Items.Add(selectAllItem);
        textBox.ContextFlyout = flyout;
#endif
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
                BeginSmoothWindowResize();
                _resizeStart = GetRegion();
                break;
            case GestureStatus.Running:
                int nw = Math.Max(140, _resizeStart.W + (int)Math.Round(e.TotalX * density));
                int nh = Math.Max(90, _resizeStart.H + (int)Math.Round(e.TotalY * density));
                MoveResize(_resizeStart.X, _resizeStart.Y, nw, nh);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                EndSmoothWindowResize();
                break;
        }
    }

    private void OnPinchSurface(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (_busy || DictionaryPanel.IsVisible || _selectedDictionaryBlock != null)
            return;

        switch (e.Status)
        {
            case GestureStatus.Started:
                _dragging = true;
                BeginSmoothWindowResize();
                _resizeStart = GetRegion();
                break;
            case GestureStatus.Running:
                ResizeWindowAroundCenter(Math.Clamp(e.Scale, PinchMinFactor, PinchMaxFactor), _resizeStart);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                EndSmoothWindowResize();
                break;
        }
    }

    private void BeginWheelResize()
    {
        if (_wheelResizing)
            return;

        _wheelResizing = true;
        _dragging = true;
        BeginSmoothWindowResize();
    }

    private void ScheduleWheelResizeEnd()
    {
        if (_wheelResizeEndTimer == null)
        {
            _wheelResizeEndTimer = Dispatcher.CreateTimer();
            _wheelResizeEndTimer.Interval = TimeSpan.FromMilliseconds(180);
            _wheelResizeEndTimer.Tick += (_, _) =>
            {
                _wheelResizeEndTimer?.Stop();
                _wheelResizeEndTimer = null;
                _wheelResizing = false;
                EndSmoothWindowResize();
            };
        }

        _wheelResizeEndTimer.Stop();
        _wheelResizeEndTimer.Start();
    }

    private void StopWheelResizeTimer()
    {
        _wheelResizeEndTimer?.Stop();
        _wheelResizeEndTimer = null;
        _wheelResizing = false;
        _dragging = false;
        _drawable.ManipulatingGlass = false;
        Canvas.Invalidate();
    }

    private void BeginSmoothWindowResize()
    {
        _drawable.ManipulatingGlass = true;
        Canvas.Invalidate();
    }

    private async void EndSmoothWindowResize()
    {
        _drawable.ManipulatingGlass = false;
        _dragging = false;
        _wheelResizing = false;
        Canvas.Invalidate();

        try
        {
            for (var i = 0; _busy && i < 6; i++)
                await Task.Delay(16);

            if (_liveMode)
                await LiveTickAsync(force: true);
        }
        catch (Exception ex)
        {
            MirrorLog.Error("End smooth resize refresh", ex);
        }
        finally
        {
            Canvas.Invalidate();
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

    private void ResizeWindowAroundCenter(double factor, (int X, int Y, int W, int H) region)
    {
        if (factor <= 0 || region.W <= 0 || region.H <= 0)
            return;

        var density = DisplayDensity();
        var minW = Math.Max(320, (int)Math.Round(420 * density));
        var minH = Math.Max(220, (int)Math.Round(260 * density));
        var maxW = int.MaxValue;
        var maxH = int.MaxValue;
        try
        {
            var display = DeviceDisplay.MainDisplayInfo;
            if (display.Width > 0 && display.Height > 0)
            {
                maxW = Math.Max(minW, (int)Math.Round(display.Width * 0.96));
                maxH = Math.Max(minH, (int)Math.Round(display.Height * 0.92));
            }
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Read display bounds for mirror resize", ex);
        }

        var nextW = Math.Clamp((int)Math.Round(region.W * factor), minW, maxW);
        var nextH = Math.Clamp((int)Math.Round(region.H * factor), minH, maxH);
        if (nextW == region.W && nextH == region.H)
            return;

        var nextX = region.X + (region.W - nextW) / 2;
        var nextY = region.Y + (region.H - nextH) / 2;
        MoveResize(nextX, nextY, nextW, nextH);
        Canvas.Invalidate();
        SetStatus($"↔ المرآة {nextW / density:0}×{nextH / density:0}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private void SetStatus(string text) => LblStatus.Text = text;

    private void SetTranslationSource(TranslationSourceKind source, string label)
    {
        SourceBadge.IsVisible = true;
        var sourceText = source switch
        {
            TranslationSourceKind.SarmadGateway => WithDetail("سرمد AI", label),
            TranslationSourceKind.MachineTranslationFallback => WithDetail("ترجمة آلية احتياطية — ليست نمطًا أكاديميًا", label),
            TranslationSourceKind.Mixed => WithDetail("مصادر مختلطة", label),
            TranslationSourceKind.OriginalTextFallback => WithDetail("النص الأصلي", label),
            _ => string.IsNullOrWhiteSpace(label) ? "غير معروف" : label,
        };
        LblTranslationSource.Text = $"مصدر الترجمة: {sourceText}";
    }

    private static string WithDetail(string arabicLabel, string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return arabicLabel;

        var normalized = detail.Trim();
        if (normalized.Equals("Sarmad AI", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("MT fallback (non-academic)", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("original fallback", StringComparison.OrdinalIgnoreCase))
        {
            return arabicLabel;
        }

        return $"{arabicLabel} — {LocalizeTranslationSourceDetail(normalized)}";
    }

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
