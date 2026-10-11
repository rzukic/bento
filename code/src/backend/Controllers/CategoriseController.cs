using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services.Ai;

namespace Bento.Api.Controllers;

/// <summary>
/// Categorising rows (Step 4.8): suggestions for a link or choice column — from
/// rules, earlier rows and, when asked, the AI — and the rules themselves.
/// Filling a column writes rows, so every route needs <c>editor</c> or <c>api</c>;
/// the AI part is for signed-in users only.
/// </summary>
[ApiController]
[Route(ApiRoutes.Categorise + "/{table}")]
[RequireRole("editor", "api")]
public sealed class CategoriseController : ControllerBase
{
    private readonly CategoriseService _categorise;

    public CategoriseController(CategoriseService categorise)
    {
        _categorise = categorise;
    }

    [HttpPost("suggest")]
    public async Task<IActionResult> Suggest(string table, [FromBody] CategoriseService.SuggestRequest request, CancellationToken ct)
    {
        var signedIn = User.FindFirstValue("auth_method") is not ("api_key" or "oauth");
        var caller = signedIn ? McpController.Caller(HttpContext) : null;
        return Ok(new { data = await _categorise.SuggestAsync(table, request, caller, ct) });
    }

    [HttpGet("rules")]
    public async Task<IActionResult> ListRules(string table, [FromQuery] string? column) =>
        Ok(new { data = await _categorise.ListRulesAsync(table, string.IsNullOrWhiteSpace(column) ? null : column.Trim()) });

    /// <summary>Saves one or more rules (<c>{ rules: [...] }</c>); the same keyword for the same column replaces the old rule.</summary>
    [HttpPost("rules")]
    public async Task<IActionResult> SaveRules(string table, [FromBody] SaveRulesRequest request)
    {
        var rules = await _categorise.SaveRulesAsync(table, request.Rules ?? [], User.GetUserId());
        return Ok(new { data = rules });
    }

    [HttpDelete("rules/{id:long}")]
    public async Task<IActionResult> DeleteRule(string table, long id)
    {
        await _categorise.DeleteRuleAsync(table, id);
        return NoContent();
    }

    public sealed record SaveRulesRequest
    {
        public IReadOnlyList<CategoriseService.RuleRequest>? Rules { get; init; }
    }
}
