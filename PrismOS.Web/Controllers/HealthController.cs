using Microsoft.AspNetCore.Mvc;

namespace PrismOS.Web.Controllers;

[Route("healthz")]
public sealed class HealthController : ControllerBase
{
    /// <summary>Checks whether the package repository service is responding.</summary>
    [HttpGet]
    public ContentResult Get() => Content("ok\n", "text/plain; charset=utf-8");
}
