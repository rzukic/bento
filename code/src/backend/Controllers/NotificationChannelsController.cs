using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Models;
using Bento.Api.Services.Notifications;

namespace Bento.Api.Controllers;

/// <summary>
/// Notification channels (Step 5.2), admins only: email over SMTP, Slack and
/// signed webhooks. Secrets go in and are never sent back; a test send says
/// at once whether a channel works, and every delivery is logged.
/// </summary>
[ApiController]
[Route(ApiRoutes.NotificationChannels)]
[RequireRole("admin")]
public sealed class NotificationChannelsController(NotificationService notifications) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok(new { data = await notifications.ListAsync() });

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) => Ok(new { data = await notifications.GetAsync(id) });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] NotificationChannelRequest request) =>
        StatusCode(StatusCodes.Status201Created, new { data = await notifications.CreateAsync(request, User.GetUserId()) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] NotificationChannelRequest request) =>
        Ok(new { data = await notifications.UpdateAsync(id, request) });

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await notifications.DeleteAsync(id);
        return NoContent();
    }

    /// <summary>Sends a test; the answer is the delivery, also a failed one with its error.</summary>
    [HttpPost("{id:long}/test")]
    public async Task<IActionResult> Test(long id) => Ok(new { data = await notifications.TestAsync(id, HttpContext.RequestAborted) });

    [HttpGet("{id:long}/deliveries")]
    public async Task<IActionResult> Deliveries(long id, [FromQuery] int limit = 20) =>
        Ok(new { data = await notifications.DeliveriesAsync(id, limit) });
}
