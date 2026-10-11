using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>Workspace settings for admins: the locale and currency numbers are written in, the time zone, week and year start (everyone reads them from /setup/status).</summary>
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

    /// <param name="TimeZone">Left out: kept; "": the server's.</param>
    public sealed record SettingsRequest(string? Locale, string? Currency, string? TimeZone = null, string? WeekStart = null, int? YearStart = null);

    [HttpGet]
    public async Task<IActionResult> Get() => Ok(new { data = await _settings.GetAsync() });

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] SettingsRequest request) =>
        Ok(new { data = await _settings.SetAsync(new WorkspaceSettings.Changes(request.Locale, request.Currency, request.TimeZone, request.WeekStart, request.YearStart)) });
}
