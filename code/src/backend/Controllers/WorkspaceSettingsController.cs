using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>Workspace settings for admins: the locale and currency numbers are written in (everyone reads them from /setup/status).</summary>
[ApiController]
[Route(ApiRoutes.WorkspaceSettings)]
[RequireRole("admin")]
public sealed class WorkspaceSettingsController : ControllerBase
{
    private readonly WorkspaceSettings _settings;

    public WorkspaceSettingsController(WorkspaceSettings settings)
    {
        _settings = settings;
    }

    public sealed record SettingsRequest(string? Locale, string? Currency);

    [HttpGet]
    public async Task<IActionResult> Get() => Ok(new { data = await _settings.GetAsync() });

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] SettingsRequest request) =>
        Ok(new { data = await _settings.SetAsync(request.Locale, request.Currency) });
}
