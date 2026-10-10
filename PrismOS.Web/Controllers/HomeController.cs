using Microsoft.AspNetCore.Mvc;

namespace PrismOS.Web.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();

    [HttpGet("/catalog")]
    public IActionResult Catalog() => View();

    [HttpGet("/manifest/{id}/{version}")]
    public IActionResult Manifest(string id, string version)
    {
        ViewData["PackageId"] = id;
        ViewData["PackageVersion"] = version;
        return View();
    }
}
