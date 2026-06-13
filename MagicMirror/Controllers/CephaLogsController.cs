using Microsoft.AspNetCore.Mvc;

namespace MagicMirror.Controllers;

/// <summary>
/// Development diagnostics — structured log viewer at /cepha/logs.
/// All log data is sourced from client-side CephaSysLog (in-memory + IndexedDB).
/// </summary>
[Route("/cepha")]
public class CephaLogsController : Controller
{
    [Route("logs")]
    public IActionResult Logs()
    {
        return View();
    }
}