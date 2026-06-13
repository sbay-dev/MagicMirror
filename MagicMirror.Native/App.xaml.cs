using Microsoft.Extensions.DependencyInjection;
using MagicMirror.Native.Mirror;

namespace MagicMirror.Native;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		HookGlobalExceptionLogging();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}

	/// <summary>Routes otherwise-fatal exceptions (incl. the GraphicsView render thread) to mirror.log.</summary>
	private static void HookGlobalExceptionLogging()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			MirrorLog.Error("AppDomain.UnhandledException", e.ExceptionObject as Exception);

		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			MirrorLog.Error("UnobservedTaskException", e.Exception);
			e.SetObserved();
		};

#if WINDOWS
		Microsoft.UI.Xaml.Application.Current.UnhandledException += (_, e) =>
		{
			MirrorLog.Error("WinUI.UnhandledException: " + e.Message, e.Exception);
			e.Handled = true; // keep the app alive; the overlay shows the error in its status pill
		};
#endif
	}
}