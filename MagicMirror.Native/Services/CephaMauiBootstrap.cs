using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using WasmMvcRuntime.Abstractions;
using WasmMvcRuntime.Abstractions.Views;
using WasmMvcRuntime.Core;

namespace MagicMirror.Native.Services;

/// <summary>
/// 🧬 Cepha MAUI Bootstrap — simplified CephaApp for native platforms.
/// No Worker, no JsExports, no OPFS — everything runs in-process.
/// </summary>
public sealed class CephaMauiBootstrap
{
    private readonly IServiceProvider _provider;
    private readonly IMvcEngine _mvcEngine;

    public CephaMauiBootstrap()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMvcEngine, MvcEngine>();
        services.AddSingleton<ITempDataDictionaryFactory, TempDataDictionaryFactory>();
        // v2 view pipeline — replaces the retired static ViewResult.Configure() wiring.
        services.AddSingleton<IRazorTemplateEngine, RazorTemplateEngine>();
        services.AddSingleton<IViewRenderer, ViewRenderer>();
        services.AddSingleton<ITemplateProvider, EmbeddedTemplateProvider>();
        services.AddSingleton<IViewLocator, ViewLocator>();
        _provider = services.BuildServiceProvider();
        _mvcEngine = _provider.GetRequiredService<IMvcEngine>();
    }

    public async Task<MvcResponse> NavigateAsync(string path)
    {
        using var scope = _provider.CreateScope();
        var context = new InternalHttpContext
        {
            Path = path,
            Method = "GET",
            RequestServices = scope.ServiceProvider
        };
        await _mvcEngine.ProcessRequestAsync(context);
        return new MvcResponse
        {
            StatusCode = context.StatusCode,
            ContentType = context.ContentType,
            Body = context.ResponseBody,
        };
    }

    public async Task<MvcResponse> SubmitFormAsync(string action, Dictionary<string, string>? formData)
    {
        using var scope = _provider.CreateScope();
        var context = new InternalHttpContext
        {
            Path = action,
            Method = "POST",
            RequestServices = scope.ServiceProvider
        };
        if (formData != null)
            foreach (var kvp in formData)
                context.FormData[kvp.Key] = kvp.Value;
        await _mvcEngine.ProcessRequestAsync(context);
        return new MvcResponse
        {
            StatusCode = context.StatusCode,
            ContentType = context.ContentType,
            Body = context.ResponseBody,
        };
    }

    public int RouteCount => (_mvcEngine as MvcEngine)?.GetRoutes()?.Count ?? 0;
}

public sealed class MvcResponse
{
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public string? Body { get; init; }
    public bool IsRedirect => StatusCode == 302 && !string.IsNullOrEmpty(Body);
}