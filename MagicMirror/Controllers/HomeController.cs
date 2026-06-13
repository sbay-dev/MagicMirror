using Microsoft.AspNetCore.Mvc;
using Cepha.Mvc;

namespace MagicMirror.Controllers;

[Route("/")]
[Route("/home")]
[Route("/home/index")]
public class HomeController : CephaController
{
    public IActionResult Index()
    {
        ViewBag.Title = "Home";
        ViewBag.Message = "Welcome to MagicMirror! 🧬";
        return View();
    }

    [Route("/home/privacy")]
    public IActionResult Privacy()
    {
        ViewBag.Title = "Privacy";
        return View();
    }

    [Route("/home/chat")]
    public IActionResult Chat()
    {
        ViewBag.Title = "Streaming Chat 🌊";
        return View();
    }

    /// <summary>
    /// UI Guard — receives scroll-triggered animation events from the client.
    /// CephaSysLog tracks these as APP-level UI protection audit entries.
    /// </summary>
    [Route("/api/ui/scroll-guard")]
    public IActionResult ScrollGuard()
    {
        var section = HttpContext?.Request.Query["section"].ToString() ?? "unknown";
        var animation = HttpContext?.Request.Query["animation"].ToString() ?? "fade-in";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return Json(new
        {
            logged = true,
            section,
            animation,
            timestamp,
            guard = "SysReg:APP"
        });
    }

    /// <summary>
    /// Streaming API — returns characters one-by-one as NDJSON via StreamJson().
    /// </summary>
    [Route("/api/chat/stream")]
    public IActionResult StreamMessage()
    {
        var msgValues = HttpContext?.Request.Query["message"];
        var msg = (msgValues.HasValue && msgValues.Value.Count > 0) ? msgValues.Value.ToString() : "Hello from Cepha streaming! 🧬";
        var userValues = HttpContext?.Request.Query["user"];
        var user = (userValues.HasValue && userValues.Value.Count > 0) ? userValues.Value.ToString() : "Cepha";
        return StreamJson(StreamChars(user, msg));
    }

    private static async IAsyncEnumerable<object> StreamChars(string user, string message)
    {
        yield return new { type = "start", user };

        foreach (var ch in message)
        {
            yield return new { type = "char", value = ch.ToString() };
            await Task.Delay(30);
        }

        yield return new { type = "done" };
    }
}