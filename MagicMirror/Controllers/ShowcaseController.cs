using Microsoft.AspNetCore.Mvc;

namespace MagicMirror.Controllers;

/// <summary>
/// Showcases every Razor pattern the Cepha runtime supports:
/// environment detection, Model boolean conditionals, @foreach item conditionals,
/// PassKey auth status, encrypted entities, and custom DB services.
/// </summary>
[Route("/showcase")]
public class ShowcaseController : Controller
{
    private readonly CephaEnvironment _env;

    public ShowcaseController(CephaEnvironment env) => _env = env;

    // ── 1. Environment-aware Features ──────────────────────────────
    [Route("/showcase/features")]
    public IActionResult Features()
    {
        ViewBag.Title = "⚙️ Runtime Features";
        ViewBag.IsDevelopment = _env.IsDevelopment() ? "true" : null;

        var model = new FeaturesViewModel
        {
            AppName       = "MagicMirror",
            IsWasmRuntime = true,
            HasIdentity   = true,
            HasSignalR    = true,
            HasSQLite     = true,
            HasSecureUI   = true,
            HasPassKey    = false,
            HasEntityCrypt = false,
            FeatureCount  = 6
        };
        return View(model);
    }

    // ── 2. Catalog — @foreach + @if(item.Property) ────────────────
    [Route("/showcase/catalog")]
    public IActionResult Catalog()
    {
        ViewBag.Title = "📦 Add-on Catalog";

        var addons = new List<CatalogItem>
        {
            new() { Name = "🛡️ Secure UI",        Description = "5-layer DOM integrity protection",        Size = "48 KB",  IsBound = true,  Tier = "Standard" },
            new() { Name = "🔐 PassKey Auth",      Description = "FIDO2/WebAuthn biometric authentication", Size = "120 KB", IsBound = false, Tier = "Premium" },
            new() { Name = "🗄️ EntityCrypt",       Description = "Transparent EF Core field encryption",    Size = "95 KB",  IsBound = false, Tier = "Premium" },
            new() { Name = "📡 CephaKit",          Description = "Node.js backend bridge for hybrid apps",  Size = "210 KB", IsBound = true,  Tier = "Standard" },
            new() { Name = "🐧 NetContainer",      Description = "Browser-based Linux environment",         Size = "82 MB",  IsBound = false, Tier = "Enterprise" },
            new() { Name = "📊 Benchmark Suite",    Description = "Cross-framework performance testing",     Size = "340 KB", IsBound = true,  Tier = "Standard" },
        };
        return View(addons);
    }

    // ── 3. PassKey management page ────────────────────────────────
    [Route("/showcase/passkeys")]
    public IActionResult PassKeys()
    {
        ViewBag.Title = "🔑 PassKey Manager";

        var model = new PassKeyViewModel
        {
            IsAuthenticated = false,
            UserName        = "",
            KeyCount        = 0,
            LastUsed        = "",
            DeviceName      = ""
        };
        return View(model);
    }

    // ── 4. Encrypted data vault ───────────────────────────────────
    [Route("/showcase/vault")]
    public IActionResult Vault()
    {
        ViewBag.Title = "🔒 Data Vault";

        var entries = new List<VaultEntry>
        {
            new() { Name = "API Key",      Category = "Credentials", IsEncrypted = true,  CreatedAt = "2026-01-15" },
            new() { Name = "OAuth Token",   Category = "Credentials", IsEncrypted = true,  CreatedAt = "2026-02-20" },
            new() { Name = "App Config",    Category = "Settings",    IsEncrypted = false, CreatedAt = "2026-03-01" },
            new() { Name = "User Prefs",    Category = "Settings",    IsEncrypted = false, CreatedAt = "2026-03-10" },
        };
        return View(entries);
    }
}

// ── View Models ───────────────────────────────────────────────────

public class FeaturesViewModel
{
    public string AppName { get; set; } = "";
    public bool IsWasmRuntime { get; set; }
    public bool HasIdentity { get; set; }
    public bool HasSignalR { get; set; }
    public bool HasSQLite { get; set; }
    public bool HasSecureUI { get; set; }
    public bool HasPassKey { get; set; }
    public bool HasEntityCrypt { get; set; }
    public int FeatureCount { get; set; }
}

public class CatalogItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Size { get; set; } = "";
    public bool IsBound { get; set; }
    public string Tier { get; set; } = "";
}

public class PassKeyViewModel
{
    public bool IsAuthenticated { get; set; }
    public string UserName { get; set; } = "";
    public int KeyCount { get; set; }
    public string LastUsed { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

public class VaultEntry
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsEncrypted { get; set; }
    public string CreatedAt { get; set; } = "";
}