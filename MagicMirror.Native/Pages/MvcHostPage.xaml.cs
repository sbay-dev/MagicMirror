using MagicMirror.Native.Services;
using WasmMvcRuntime.Abstractions;

namespace MagicMirror.Native.Pages;

public partial class MvcHostPage : ContentPage
{
    private CephaMauiBootstrap? _mvc;
    private NativeRenderer? _renderer;
    private bool _initialized;

    public MvcHostPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            PlatformInterop.Use(new NativePlatformInterop());
            _renderer = new NativeRenderer();
            _renderer.NavigationRequested += async (_, path) => await ProcessNavigationAsync(path);
            _renderer.FormSubmitted += async (_, args) => await HandleFormSubmitAsync(args.Action, args.Data);

            _mvc = new CephaMauiBootstrap();

            var routes = _mvc.RouteCount;
            LblRoutes.Text = $"📍 {routes} routes";
            LblPlatform.Text = $"🖥️ {DeviceInfo.Platform}";
            LblStatus.Text = "✅ Cepha ready";

            await ProcessNavigationAsync("/");
        }
        catch (Exception ex)
        {
            LblStatus.Text = $"❌ {ex.Message}";
            ShowError("Boot Error", ex);
        }
    }

    private async Task ProcessNavigationAsync(string path)
    {
        if (_mvc == null || _renderer == null) return;
        try
        {
            LblRoute.Text = path;
            LblStatus.Text = $"⏳ GET {path}";

            var response = await _mvc.NavigateAsync(path);

            if (response.IsRedirect)
            {
                await ProcessNavigationAsync(response.Body!);
                return;
            }

            if (!string.IsNullOrEmpty(response.Body))
            {
                var nativeView = _renderer.Render(response.Body);
                NativeContent.Children.Clear();
                NativeContent.Children.Add(nativeView);
                LblStatus.Text = $"✅ {response.StatusCode} — {path}";
            }
            else
            {
                Show404(path);
            }
        }
        catch (Exception ex)
        {
            LblStatus.Text = $"❌ {path}";
            ShowError($"Error: {path}", ex);
        }
    }

    private async Task HandleFormSubmitAsync(string action, Dictionary<string, string> formData)
    {
        if (_mvc == null || _renderer == null) return;
        try
        {
            LblStatus.Text = $"⏳ POST {action}";
            var response = await _mvc.SubmitFormAsync(action, formData);

            if (response.IsRedirect) { await ProcessNavigationAsync(response.Body!); return; }

            if (!string.IsNullOrEmpty(response.Body))
            {
                var nativeView = _renderer.Render(response.Body);
                NativeContent.Children.Clear();
                NativeContent.Children.Add(nativeView);
                LblStatus.Text = $"✅ POST {action}";
            }
        }
        catch (Exception ex)
        {
            LblStatus.Text = $"❌ POST {action}";
            ShowError($"Form Error: {action}", ex);
        }
    }

    private void Show404(string path)
    {
        NativeContent.Children.Clear();
        NativeContent.Children.Add(new VerticalStackLayout
        {
            Padding = new Thickness(40), Spacing = 12,
            Children =
            {
                new Label { Text = "404", FontSize = 48, FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#8B949E"), HorizontalTextAlignment = TextAlignment.Center },
                new Label { Text = $"No route for {path}", FontSize = 16,
                    TextColor = Color.FromArgb("#8B949E"), HorizontalTextAlignment = TextAlignment.Center },
                CreateNavButton("← Home", "/")
            }
        });
        LblStatus.Text = $"⚠️ 404 — {path}";
    }

    private void ShowError(string title, Exception ex)
    {
        NativeContent.Children.Clear();
        NativeContent.Children.Add(new VerticalStackLayout
        {
            Padding = new Thickness(20), Spacing = 12,
            Children =
            {
                new Label { Text = $"❌ {title}", FontSize = 20, FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#F85149") },
                new Label { Text = ex.Message, FontSize = 14, TextColor = Color.FromArgb("#E6EDF3"),
                    LineBreakMode = LineBreakMode.WordWrap },
                CreateNavButton("← Home", "/")
            }
        });
    }

    private Button CreateNavButton(string text, string path)
    {
        var btn = new Button
        {
            Text = text, BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#58A6FF"), BorderColor = Color.FromArgb("#58A6FF"),
            BorderWidth = 1, CornerRadius = 6, FontSize = 14,
            HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 8)
        };
        btn.Clicked += async (_, _) => await ProcessNavigationAsync(path);
        return btn;
    }
}