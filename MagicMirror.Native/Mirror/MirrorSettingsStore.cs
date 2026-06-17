using System.Text.Json;

namespace MagicMirror.Native.Mirror;

/// <summary>
/// Loads/saves <see cref="MirrorSettings"/> as JSON in the app data directory.
/// Single source of truth shared by the native overlay and the (web-rendered) settings UI.
/// </summary>
public sealed class MirrorSettingsStore
{
    private const string DeprecatedSarmadModel = "@cf/openai/gpt-oss-120b";
    private const string CurrentSarmadModel = "@cf/openai/gpt-oss-20b";
    private const string DeprecatedCanonicalSarmadUrl = "https://wmr-doc.pages.dev/api/sarmad/ask";
    private const string DedicatedGatewayBaseUrl = "https://magicmirror-sarmad-gateway.2sa.workers.dev";
    private const int CurrentSettingsSchemaVersion = 3;
    private const string DeprecatedTheorysTessDataPath =
        @"X:\source\THEORYS\Items\tesseract-complete-package\models\tesseract-ocr\5\tessdata";

    private readonly string _path;
    private MirrorSettings _current;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public MirrorSettingsStore()
    {
        _path = Path.Combine(FileSystem.AppDataDirectory, "mirror-settings.json");
        _current = Load();
    }

    public MirrorSettings Current => _current;

    public event EventHandler<MirrorSettings>? Changed;

    private MirrorSettings Load()
    {
        MirrorSettings settings;
        try
        {
            if (File.Exists(_path))
            {
                settings = JsonSerializer.Deserialize<MirrorSettings>(File.ReadAllText(_path), Json) ?? new MirrorSettings();
                return Normalize(settings);
            }
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Unable to load mirror settings; using defaults.", ex);
        }

        return Normalize(new MirrorSettings());
    }

    public void Save(MirrorSettings settings)
    {
        _current = Normalize(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_current, Json));
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Unable to save mirror settings.", ex);
        }
        Changed?.Invoke(this, _current);
    }

    private static MirrorSettings Normalize(MirrorSettings settings)
    {
        settings.TextScale = Math.Clamp(settings.TextScale, 0.75, 3.0);
        settings.LineSpacingScale = Math.Clamp(settings.LineSpacingScale, 0.85, 1.6);
        settings.DimAmount = Math.Clamp(settings.DimAmount, 0.0, 0.9);
        settings.TranslationBackgroundColor = MirrorAppearanceColors.NormalizeHex(
            settings.TranslationBackgroundColor, MirrorAppearanceColors.DefaultBackgroundHex);
        settings.TranslationBackgroundOpacity = Math.Clamp(settings.TranslationBackgroundOpacity, 0.0, 1.0);
        settings.TranslationTextColor = MirrorAppearanceColors.NormalizeHex(
            settings.TranslationTextColor, MirrorAppearanceColors.DefaultTextHex);
        settings.IdlePreviewFps = Math.Clamp(settings.IdlePreviewFps, 2, 30);
        settings.OcrCaptureScale = Math.Clamp(settings.OcrCaptureScale <= 0 ? 2.0 : settings.OcrCaptureScale, 1.0, 3.0);
        if (settings.SettingsSchemaVersion < 3)
            settings.AllowMachineTranslationFallback = false;
        settings.ForceMachineTranslationFallback = false;

        if (string.IsNullOrWhiteSpace(settings.TargetLanguage))
            settings.TargetLanguage = "ar";
        if (string.IsNullOrWhiteSpace(settings.AiModel) ||
            string.Equals(settings.AiModel.Trim(), DeprecatedSarmadModel, StringComparison.OrdinalIgnoreCase))
        {
            settings.AiModel = CurrentSarmadModel;
        }
        else
        {
            settings.AiModel = settings.AiModel.Trim();
        }

        settings.GatewayBaseUrl = (settings.GatewayBaseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(settings.GatewayBaseUrl))
            settings.GatewayBaseUrl = DedicatedGatewayBaseUrl;
        settings.FallbackSarmadUrl = (settings.FallbackSarmadUrl ?? "").Trim();
        if (string.Equals(settings.FallbackSarmadUrl.TrimEnd('/'), DeprecatedCanonicalSarmadUrl, StringComparison.OrdinalIgnoreCase))
            settings.FallbackSarmadUrl = "";
        settings.TessDataPath = (settings.TessDataPath ?? "").Trim();
        if (string.Equals(settings.TessDataPath, DeprecatedTheorysTessDataPath, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(settings.TessDataPath))
        {
            settings.TessDataPath = "";
        }
        settings.TesseractExePath = (settings.TesseractExePath ?? "").Trim();
        settings.SettingsSchemaVersion = CurrentSettingsSchemaVersion;

        return settings;
    }
}
