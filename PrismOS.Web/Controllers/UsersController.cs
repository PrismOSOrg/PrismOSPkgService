using Microsoft.AspNetCore.Mvc;
using PrismOS.Application.Accounts;
using PrismOS.Application.Repositories;
using PrismOS.Domain.Packages;
using PrismOS.Web.Infrastructure;
using PrismOS.Web.Models;

namespace PrismOS.Web.Controllers;

[Route("api/v1/users/me")]
public sealed class UsersController(IPackageRepository packages, IAccountStore accounts) : ControllerBase
{
    /// <summary>Returns the current user's available package favorites.</summary>
    [HttpGet("favorites")]
    public async Task<IActionResult> Favorites()
    {
        (bool provided, string? username) = await BearerAuth.ResolveAsync(HttpContext, accounts);
        if (!provided || username is null) return BearerAuth.Challenge(HttpContext);
        IReadOnlyList<string>? favorites = await accounts.GetFavoritesAsync(username,
            HttpContext.RequestAborted);
        var result = favorites!.Select(key =>
        {
            int separator = key.IndexOf('/');
            string id = key[..separator];
            string version = key[(separator + 1)..];
            return packages.TryGetPackage(id, version, out PackageRecord? package) ? package : null;
        }).Where(package => package is not null)
            .Select(package => new { package!.Id, package.Version, package.Description });
        return Ok(result);
    }

    /// <summary>Adds a package version to the current user's favorites.</summary>
    /// <param name="id">Package identifier.</param>
    /// <param name="version">Package version.</param>
    [HttpPut("favorites/{id}/{version}")]
    public async Task<IActionResult> AddFavorite(string id, string version)
    {
        (bool provided, string? username) = await BearerAuth.ResolveAsync(HttpContext, accounts);
        if (!provided || username is null) return BearerAuth.Challenge(HttpContext);
        if (!packages.TryGetPackage(id, version, out _)) return NotFound();
        await accounts.SetFavoriteAsync(username, $"{id.ToLowerInvariant()}/{version}", true,
            HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>Removes a package version from the current user's favorites.</summary>
    /// <param name="id">Package identifier.</param>
    /// <param name="version">Package version.</param>
    [HttpDelete("favorites/{id}/{version}")]
    public async Task<IActionResult> RemoveFavorite(string id, string version)
    {
        (bool provided, string? username) = await BearerAuth.ResolveAsync(HttpContext, accounts);
        if (!provided || username is null) return BearerAuth.Challenge(HttpContext);
        await accounts.SetFavoriteAsync(username, $"{id.ToLowerInvariant()}/{version}", false,
            HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>Returns download totals attributed to the current user.</summary>
    [HttpGet("downloads")]
    public async Task<IActionResult> Downloads()
    {
        (bool provided, string? username) = await BearerAuth.ResolveAsync(HttpContext, accounts);
        if (!provided || username is null) return BearerAuth.Challenge(HttpContext);
        return Ok(await accounts.GetUserDownloadsAsync(username, HttpContext.RequestAborted));
    }

    /// <summary>Returns package releases published by the current user.</summary>
    [HttpGet("releases")]
    public async Task<IActionResult> Releases()
    {
        (bool provided, string? username) = await BearerAuth.ResolveAsync(HttpContext, accounts);
        if (!provided || username is null) return BearerAuth.Challenge(HttpContext);
        return Ok(packages.GetPackagesUploadedBy(username).Select(package => new
        {
            package.Id,
            package.Version,
            package.Description,
            package.Size,
            package.Sha256
        }));
    }

    /// <summary>Updates the description of a release owned by the current user.</summary>
    /// <param name="id">Package identifier.</param>
    /// <param name="version">Package version.</param>
    [HttpPut("releases/{id}/{version}")]
    public async Task<IActionResult> UpdateReleaseDescription(string id, string version,
        [FromBody] UpdateReleaseDescriptionRequest? request)
    {
        (bool provided, string? username) = await BearerAuth.ResolveAsync(HttpContext, accounts);
        if (!provided || username is null) return BearerAuth.Challenge(HttpContext);
        if (request?.Description is null)
            return BadRequest(new { error = "description is required" });
        if (request.Description.Length > 120)
            return BadRequest(new { error = "description must be 120 characters or fewer" });

        PackageRecord? package = await packages.UpdateDescriptionAsync(id, version, username,
            request.Description, HttpContext.RequestAborted);
        if (package is null) return NotFound();

        return Ok(new
        {
            package.Id,
            package.Version,
            package.Description,
            package.Size,
            package.Sha256
        });
    }
}
