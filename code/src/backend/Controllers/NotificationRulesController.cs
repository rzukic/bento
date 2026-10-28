using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Models;
using Bento.Api.Services.Notifications;

namespace Bento.Api.Controllers;

/// <summary>
/// Notification rules (Step 5.3), admins only: conditions as formulas, budgets
/// that run over, failed imports and price updates, scheduled reports. Check one now, or send what
/// it would say to try the wording and the channels.
/// </summary>
[ApiController]
[Route(ApiRoutes.NotificationRules)]
[RequireRole("admin")]
public sealed class NotificationRulesController(NotificationRuleService rules) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok(new { data = await rules.ListAsync() });

    /// <summary>The channels a rule can speak on, by name only (Step 5.14): their addresses and secrets stay in Settings.</summary>
    [HttpGet("channels")]
    public async Task<IActionResult> Channels([FromServices] Services.Notifications.NotificationService notifications) =>
        Ok(new { data = (await notifications.ListAsync()).Select(c => new { c.Id, c.Name, c.Kind }) });

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) => Ok(new { data = await rules.GetAsync(id) });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] NotificationRuleRequest request) =>
        StatusCode(StatusCodes.Status201Created, new { data = await rules.CreateAsync(request, User.GetUserId()) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] NotificationRuleRequest request) =>
        Ok(new { data = await rules.UpdateAsync(id, request) });

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await rules.DeleteAsync(id);
        return NoContent();
    }

    /// <summary>A report as it would go out today, from the editor's unsaved configuration.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] ReportPreviewRequest request) =>
        Ok(new { data = await rules.PreviewAsync(request, HttpContext.RequestAborted) });

    /// <summary>Checks now; speaks if the condition newly holds. A report is only put together.</summary>
    [HttpPost("{id:long}/check")]
    public async Task<IActionResult> Check(long id) => Ok(new { data = await rules.CheckAsync(id, HttpContext.RequestAborted) });

    /// <summary>Sends what the rule would say now, whether or not it holds.</summary>
    [HttpPost("{id:long}/test")]
    public async Task<IActionResult> Test(long id) => Ok(new { data = await rules.TestAsync(id, HttpContext.RequestAborted) });
}
