// 🧬 Cepha Application
using System.Runtime.Versioning;
using MagicMirror.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using EntityCrypt.EFCore.Matryoshka;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using WasmMvcRuntime.Identity.Security;

[assembly: SupportedOSPlatform("browser")]

var builder = WebApplication.CreateBuilder(args);

// Structured logging — environment-aware EF Core log routing
// Production: EF Core → memory only (SysLog server) — NEVER to browser console
// Development: EF Core Warn+ → console + memory (for debugging)
var nlogConfig = new LoggingConfiguration();
var consoleFmt = "${level:uppercase=true:padding=-5} | ${logger:shortName=true} | ${message}${onexception:inner=${newline}${exception:format=tostring}}";
var logFmt     = "${longdate}|${level:uppercase=true}|${logger:shortName=true}|${message}${onexception:inner=${newline}${exception:format=tostring}}";
var memoryTarget = new MemoryTarget("memory") { MaxLogsCount = 1000, Layout = logFmt };
var consoleTarget = new ConsoleTarget("console") { Layout = consoleFmt };

if (builder.Environment.IsDevelopment())
{
    // Dev: EF Core Warn+ to console + memory for debugging
    nlogConfig.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, consoleTarget, "Microsoft.EntityFrameworkCore.*", final: true);
    nlogConfig.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, memoryTarget, "Microsoft.EntityFrameworkCore.*", final: true);
}
else
{
    // Production: EF Core → memory only (feeds SysLog), console blocked completely
    nlogConfig.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget, "Microsoft.EntityFrameworkCore.*", final: true);
}

nlogConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, consoleTarget);
nlogConfig.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, memoryTarget);
LogManager.Configuration = nlogConfig;

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=app.db";
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseSqlite(connectionString);
    options.UseMatryoshka(EntityCryptSecurityPolicy.GetDatabaseMasterKey("{{name}}-app-db"));
});

builder.Services.AddLogging(lb => lb.ClearProviders().AddNLog());
builder.Services.AddControllersWithViews();

var app = builder.Build();

app.UseExceptionHandler("/Home/Error");
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();