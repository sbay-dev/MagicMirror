using System.Text.Json;

namespace MagicMirror.Native.Mirror;

/// <summary>
/// Loads/saves <see cref="MirrorSettings"/> as JSON in the app data directory.
/// Single source of truth shared by the native overlay and the (web-rendered) settings UI.
/// </summary>
public sealed class MirrorSettingsStore
{
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

        if (string.IsNullOrWhiteSpace(settings.TargetLanguage))
            settings.TargetLanguage = "ar";

        return settings;
    }
}
