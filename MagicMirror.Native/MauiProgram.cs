using Microsoft.Extensions.Logging;
using MagicMirror.Native.Mirror;
using MagicMirror.Native.Pages;

namespace MagicMirror.Native;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// ── Magic Mirror services ──────────────────────────────────────────
		builder.Services.AddSingleton<MirrorSettingsStore>();
		builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(40) });
		builder.Services.AddSingleton<ITranslationService, SarmadTranslationService>();

		// Primary OCR engine (Tesseract) is cross-platform; OS OCR is added per-platform.
		builder.Services.AddSingleton<IOcrEngine, TesseractOcrEngine>();
#if WINDOWS
		builder.Services.AddSingleton<IScreenCapture, MagicMirror.Native.Platforms.Windows.WindowsScreenCapture>();
		builder.Services.AddSingleton<IOcrEngine, MagicMirror.Native.Platforms.Windows.WindowsMediaOcrEngine>();
		builder.Services.AddSingleton<IWindowTextProvider, MagicMirror.Native.Platforms.Windows.WindowsUiaTextProvider>();
#else
		builder.Services.AddSingleton<IScreenCapture, NullScreenCapture>();
		builder.Services.AddSingleton<IWindowTextProvider, NullWindowTextProvider>();
#endif
		builder.Services.AddSingleton<OcrService>();
		builder.Services.AddSingleton<MirrorEngine>();

		// Pages (resolved by Shell DataTemplate via DI)
		builder.Services.AddTransient<MirrorPage>();
		builder.Services.AddTransient<MvcHostPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
