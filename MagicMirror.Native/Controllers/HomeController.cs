using Microsoft.AspNetCore.Mvc;

namespace MagicMirror.Native.Controllers;

/// <summary>
/// Landing controller rendered through the WasmMvcRuntime → NativeRenderer pipeline.
/// The interactive "magic mirror" controls live in the native MirrorPage; this view
/// is the in-app About/landing surface proving the MVC engine runs in-process.
/// </summary>
[Route("/")]
[Route("/home")]
[Route("/home/index")]
public class HomeController : Controller
{
    public IActionResult Index()
    {
        ViewBag.Title = "Magic Mirror";
        ViewBag.Message = "المرآة السحرية — Magic Mirror";
        return View();
    }
}
