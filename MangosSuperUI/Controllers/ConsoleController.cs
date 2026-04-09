using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

public class ConsoleController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}
