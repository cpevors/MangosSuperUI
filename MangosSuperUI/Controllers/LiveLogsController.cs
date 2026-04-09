using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

public class LiveLogsController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
