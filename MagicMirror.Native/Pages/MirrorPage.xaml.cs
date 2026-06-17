using MagicMirror.Native.Mirror;

namespace MagicMirror.Native.Pages;

/// <summary>
/// Control centre for the mirror: launches the glass overlay window and edits the shared
/// <see cref="MirrorSettings"/> (target language, OCR engine, dimming, AI gateway, Tesseract).
/// </summary>
public partial class MirrorPage : ContentPage
{
    private readonly MirrorEngine _engine;
    private readonly MirrorSettingsStore _settings;
    private readonly GlossaryMemoryStore _glossaryMemory;
    private readonly IReadOnlyList<IOcrEngine> _ocrEngines;
    private readonly ITranslationService _translator;

    private static readonly (string Label, string Code)[] Targets =
    {
        ("Arabic · العربية", "ar"), ("English", "en"), ("French", "fr"), ("Spanish", "es"),
        ("German", "de"), ("Turkish", "tr"), ("Urdu", "ur"), ("Persian", "fa"),
    };

    private static readonly (string Label, string Code)[] Sources =
    {
        ("Auto", "auto"), ("English", "en"), ("Arabic", "ar"), ("French", "fr"),
        ("Spanish", "es"), ("German", "de"),
    };

    private static readonly (string Label, OcrEnginePreference Value)[] OcrModes =
    {
        ("Tesseract → OS fallback", OcrEnginePreference.TesseractThenNative),
        ("OS OCR only", OcrEnginePreference.NativeOnly),
        ("Tesseract only", OcrEnginePreference.TesseractOnly),
    };

    private static readonly (string Label, OverlayLayoutMode Value)[] LayoutModes =
    {
        ("Match original sizes", OverlayLayoutMode.MatchOriginal),
        ("Readable (enlarge small)", OverlayLayoutMode.Readable),
        ("Uniform size", OverlayLayoutMode.Uniform),
    };

    public MirrorPage(
        MirrorEngine engine,
        MirrorSettingsStore settings,
        GlossaryMemoryStore glossaryMemory,
        IEnumerable<IOcrEngine> ocrEngines,
        ITranslationService translator)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _glossaryMemory = glossaryMemory;
        _ocrEngines = ocrEngines.ToList();
        _translator = translator;

        foreach (var t in Targets) PickerTarget.Items.Add(t.Label);
        foreach (var s in Sources) PickerSource.Items.Add(s.Label);
        foreach (var o in OcrModes) PickerOcr.Items.Add(o.Label);
        foreach (var l in LayoutModes) PickerLayout.Items.Add(l.Label);

        LoadIntoControls(_settings.Current);
        Loaded += async (_, _) => await RefreshStatusAsync();
        Loaded += OnPageLoadedAnimate;

        if (Environment.GetEnvironmentVariable("MM_SELFTEST") == "1")
            Loaded += async (_, _) => await RunSelfTestAsync();
        if (Environment.GetEnvironmentVariable("MM_DIAG") == "1")
            Loaded += (_, _) => Dispatcher.Dispatch(() => OnOpenMirror(this, EventArgs.Empty));
    }

    private bool _entranceDone;
    private bool _ambient;

    /// <summary>Cinematic entrance + ambient looping glow/orb-drift for the futuristic feel.</summary>
    private void OnPageLoadedAnimate(object? sender, EventArgs e)
    {
        if (_entranceDone) return;
        _entranceDone = true;

        // Entrance: fade + slide each top-level card in sequence.
        if (BtnOpen?.Parent?.Parent is Microsoft.Maui.Controls.Layout root)
        {
            uint delay = 0;
            foreach (var child in root.Children.OfType<VisualElement>())
            {
                child.Opacity = 0;
                child.TranslationY = 22;
                var ve = child;
                var d = delay;
                Dispatcher.Dispatch(async () =>
                {
                    await Task.Delay((int)d);
                    _ = ve.FadeTo(1, 380, Easing.CubicOut);
                    await ve.TranslateTo(0, 0, 420, Easing.CubicOut);
                });
                delay += 70;
            }
        }
        StartAmbient();
    }

    /// <summary>Looping ambient motion (hero breathing + orb drift); paused while the tab is hidden.</summary>
    private void StartAmbient()
    {
        if (_ambient) return;
        _ambient = true;

        Dispatcher.Dispatch(async () =>
        {
            while (_ambient && HeroIcon != null)
            {
                await HeroIcon.ScaleTo(1.10, 1400, Easing.SinInOut);
                await HeroIcon.ScaleTo(1.00, 1400, Easing.SinInOut);
            }
        });

        Dispatcher.Dispatch(async () =>
        {
            while (_ambient && OrbCyan != null)
            {
                await Task.WhenAll(OrbCyan.TranslateTo(20, 30, 4000, Easing.SinInOut),
                                   OrbViolet.TranslateTo(-24, 18, 4000, Easing.SinInOut));
                await Task.WhenAll(OrbCyan.TranslateTo(0, 0, 4000, Easing.SinInOut),
                                   OrbViolet.TranslateTo(0, 0, 4000, Easing.SinInOut));
            }
        });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_entranceDone) StartAmbient();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _ambient = false; // pause ambient loops while the tab is hidden
    }

    /// <summary>
    /// Headless pipeline check (set env MM_SELFTEST=1). Exercises translation-only and the full
    /// capture→OCR→translate flow, logging each stage to mirror.log so a crashing stage is
    /// identifiable without the GUI. Writes a SELFTEST DONE marker on success.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        MirrorLog.Info("=== SELFTEST START ===  log=" + MirrorLog.Path);
        MirrorLog.Info($"captureAvailable={_engine.CaptureAvailable}");
        try
        {
            var t = await _translator.TranslateBatchAsync(
                new[] { "File", "Save changes", "Settings" }, _settings.Current.TargetLanguage, _settings.Current);
            MirrorLog.Info($"translate-only ({t.SourceLabel}): " + string.Join(" | ", t.Lines));
        }
        catch (Exception ex) { MirrorLog.Error("translate-only", ex); }

        try
        {
            var res = await _engine.TranslateRegionAsync(80, 80, 800, 320);
            MirrorLog.Info($"pipeline status={res.Status} blocks={res.Blocks.Count}");
            foreach (var b in res.Blocks.Take(6))
                MirrorLog.Info($"  [{b.X},{b.Y},{b.Width}x{b.Height}] '{b.OriginalText}' -> '{b.TranslatedText}' font={b.Font.Family}/{b.Font.SizePt}");
        }
        catch (Exception ex) { MirrorLog.Error("pipeline", ex); }

        MirrorLog.Info("=== SELFTEST DONE ===");
    }

    private void LoadIntoControls(MirrorSettings s)
    {
        PickerTarget.SelectedIndex = Math.Max(0, Array.FindIndex(Targets, t => t.Code == s.TargetLanguage));
        PickerSource.SelectedIndex = Math.Max(0, Array.FindIndex(Sources, x => x.Code == s.SourceLanguageHint));
        PickerOcr.SelectedIndex = Math.Max(0, Array.FindIndex(OcrModes, x => x.Value == s.OcrEngine));
        SwitchWindowText.IsToggled = s.UseWindowText;
        SliderDim.Value = s.DimAmount;
        EntryTranslationBackgroundColor.Text = s.TranslationBackgroundColor;
        SliderTranslationBackgroundOpacity.Value = s.TranslationBackgroundOpacity;
        EntryTranslationTextColor.Text = s.TranslationTextColor;
        SliderScale.Value = s.TextScale;
        SliderLineSpacing.Value = s.LineSpacingScale;
        PickerLayout.SelectedIndex = Math.Max(0, Array.FindIndex(LayoutModes, x => x.Value == s.LayoutMode));
        SliderFps.Value = s.IdlePreviewFps;
        EntryGateway.Text = s.GatewayBaseUrl;
        EntryModel.Text = s.AiModel;
        SwitchMtFallback.IsToggled = s.AllowMachineTranslationFallback;
        EntryTessLangs.Text = s.TesseractLanguages;
        EntryTessData.Text = s.TessDataPath;
        EntryTessExe.Text = s.TesseractExePath;
        LblDim.Text = $"Dimming: {s.DimAmount:0.00}";
        LblTranslationBackgroundOpacity.Text = $"Text background opacity: {s.TranslationBackgroundOpacity:0.00}";
        LblScale.Text = $"Text size: {s.TextScale * 100:0}%";
        LblLineSpacing.Text = $"Line spacing: {s.LineSpacingScale:0.00}×";
        LblFps.Text = $"Live preview FPS: {s.IdlePreviewFps:0}";
    }

    private async Task RefreshStatusAsync()
    {
        LblCapture.Text = _engine.CaptureAvailable ? "Capture: ✅ available" : "Capture: ⚠️ not on this platform";

        var parts = new List<string>();
        foreach (var e in _ocrEngines)
        {
            bool ok;
            try { ok = await e.IsAvailableAsync(); } catch { ok = false; }
            parts.Add($"{e.Name} {(ok ? "✅" : "—")}");
        }
        LblOcr.Text = "OCR: " + (parts.Count > 0 ? string.Join("  ·  ", parts) : "none")
            + (_engine.WindowTextAvailable ? "  ·  Window-text ✅" : "");

        var s = _settings.Current;
        LblAi.Text = "AI: " + (string.IsNullOrWhiteSpace(s.GatewayBaseUrl)
            ? $"not configured · {(s.AllowMachineTranslationFallback ? "MT prompt available (non-academic)" : "no MT fallback")} · {s.AiModel}"
            : $"{s.GatewayBaseUrl} · {s.AiModel}");
    }

    private void OnOpenMirror(object? sender, EventArgs e)
    {
        var overlay = new MirrorOverlayPage(_engine, _settings, _glossaryMemory);
        var win = new Window(overlay) { Title = "Magic Mirror Overlay", Width = 540, Height = 340, X = 240, Y = 200 };
        Application.Current?.OpenWindow(win);
    }

    private void OnDimChanged(object? sender, ValueChangedEventArgs e) => LblDim.Text = $"Dimming: {e.NewValue:0.00}";

    private void OnTranslationBackgroundOpacityChanged(object? sender, ValueChangedEventArgs e)
        => LblTranslationBackgroundOpacity.Text = $"Text background opacity: {e.NewValue:0.00}";

    private void OnScaleChanged(object? sender, ValueChangedEventArgs e) => LblScale.Text = $"Text size: {e.NewValue * 100:0}%";

    private void OnLineSpacingChanged(object? sender, ValueChangedEventArgs e) => LblLineSpacing.Text = $"Line spacing: {e.NewValue:0.00}×";

    private void OnFpsChanged(object? sender, ValueChangedEventArgs e) => LblFps.Text = $"Live preview FPS: {e.NewValue:0}";

    private async void OnSave(object? sender, EventArgs e)
    {
        var s = _settings.Current;
        s.TargetLanguage = Targets[Clamp(PickerTarget.SelectedIndex, Targets.Length)].Code;
        s.SourceLanguageHint = Sources[Clamp(PickerSource.SelectedIndex, Sources.Length)].Code;
        s.OcrEngine = OcrModes[Clamp(PickerOcr.SelectedIndex, OcrModes.Length)].Value;
        s.UseWindowText = SwitchWindowText.IsToggled;
        s.DimAmount = SliderDim.Value;
        s.TranslationBackgroundColor = MirrorAppearanceColors.NormalizeHex(
            EntryTranslationBackgroundColor.Text, MirrorAppearanceColors.DefaultBackgroundHex);
        s.TranslationBackgroundOpacity = SliderTranslationBackgroundOpacity.Value;
        s.TranslationTextColor = MirrorAppearanceColors.NormalizeHex(
            EntryTranslationTextColor.Text, MirrorAppearanceColors.DefaultTextHex);
        s.TextScale = SliderScale.Value;
        s.LineSpacingScale = SliderLineSpacing.Value;
        s.LayoutMode = LayoutModes[Clamp(PickerLayout.SelectedIndex, LayoutModes.Length)].Value;
        s.IdlePreviewFps = SliderFps.Value;
        s.GatewayBaseUrl = (EntryGateway.Text ?? "").Trim();
        s.AiModel = string.IsNullOrWhiteSpace(EntryModel.Text) ? "@cf/openai/gpt-oss-20b" : EntryModel.Text.Trim();
        s.AllowMachineTranslationFallback = SwitchMtFallback.IsToggled;
        s.TesseractLanguages = string.IsNullOrWhiteSpace(EntryTessLangs.Text) ? "eng+ara" : EntryTessLangs.Text.Trim();
        s.TessDataPath = (EntryTessData.Text ?? "").Trim();
        s.TesseractExePath = (EntryTessExe.Text ?? "").Trim();
        _settings.Save(s);
        LoadIntoControls(_settings.Current);

        LblSaved.Text = "✅ Saved " + DateTime.Now.ToString("HH:mm:ss");
        await RefreshStatusAsync();
    }

    private static int Clamp(int idx, int len) => idx < 0 || idx >= len ? 0 : idx;
}
