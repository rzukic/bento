using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Exceptions;
using Bento.Api.Services.Ai;

namespace Bento.Api.Controllers;

/// <summary>
/// Bento's AI (Step 4.7): status for everyone, settings and the key for admins,
/// the usage log (own calls; admins all), and "explain this". API keys and
/// connected apps cannot spend the AI budget — they bring their own model.
/// </summary>
[ApiController]
[Route(ApiRoutes.Ai)]
[RequireRole("viewer")]
public sealed class AiController : ControllerBase
{
    private readonly AiSettingsService _settings;
    private readonly ExplainService _explain;

    public AiController(AiSettingsService settings, ExplainService explain)
    {
        _settings = settings;
        _explain = explain;
    }

    /// <summary>Whether the AI actions should show, and with which model.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        EnsureSignedInUser();
        var settings = await _settings.GetAsync();
        var spent = await _settings.SpentThisMonthAsync();
        return Ok(new { data = new { enabled = settings.Enabled, configured = settings.Configured, model = settings.Model, budgetLeft = spent < settings.MonthlyLimitUsd } });
    }

    [HttpGet("settings")]
    [RequireRole("admin")]
    public async Task<IActionResult> GetSettings()
    {
        EnsureSignedInUser();
        return Ok(new { data = await SettingsResponse() });
    }

    public sealed record SettingsRequest
    {
        /// <summary>A new Anthropic API key; empty removes the stored one; missing keeps it.</summary>
        public string? ApiKey { get; init; }
        public string? Model { get; init; }
        public decimal? MonthlyLimitUsd { get; init; }
        public bool? Enabled { get; init; }
    }

    [HttpPut("settings")]
    [RequireRole("admin")]
    public async Task<IActionResult> SaveSettings([FromBody] SettingsRequest request)
    {
        EnsureSignedInUser();
        await _settings.SaveAsync(request.ApiKey, request.Model, request.MonthlyLimitUsd, request.Enabled);
        return Ok(new { data = await SettingsResponse() });
    }

    [HttpGet("usage")]
    public async Task<IActionResult> Usage([FromQuery] int limit = 50)
    {
        EnsureSignedInUser();
        var calls = await _settings.ListUsageAsync(User.GetRole() == "admin" ? null : User.GetUserId(), limit);
        return Ok(new { data = calls });
    }

    [HttpPost("explain")]
    public async Task<IActionResult> Explain([FromBody] ExplainService.ExplainRequest request, CancellationToken ct)
    {
        EnsureSignedInUser();
        try
        {
            var caller = McpController.Caller(HttpContext);
            return Ok(new { data = await _explain.ExplainAsync(caller, request, ct) });
        }
        catch (AiProviderException ex)
        {
            return StatusCode(ex.Status, new { error = new { code = "AI_FAILED", message = ex.Message } });
        }
    }

    private async Task<object> SettingsResponse()
    {
        var settings = await _settings.GetAsync();
        return new
        {
            configured = settings.Configured,
            enabled = settings.Enabled,
            model = settings.Model,
            monthlyLimitUsd = settings.MonthlyLimitUsd,
            spentThisMonthUsd = await _settings.SpentThisMonthAsync(),
            models = AiSettingsService.Models.Select(m => new { id = m.Id, name = m.Name, description = m.Description })
        };
    }

    private void EnsureSignedInUser()
    {
        if (User.FindFirstValue("auth_method") is "api_key" or "oauth")
            throw new ApiForbiddenException("Bento's AI is for signed-in users; scripts and connected apps use their own model.");
    }
}
