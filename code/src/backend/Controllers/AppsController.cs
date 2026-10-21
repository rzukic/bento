using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Configuration;
using Bento.Api.Models;
using Bento.Api.Services.Apps;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Controllers;

/// <summary>
/// Apps (Step 5.4), admins only: calendars made of tables, and the passwords
/// devices sign in with. A new password is shown once, with the addresses to
/// enter on the device.
/// </summary>
[ApiController]
[Route(ApiRoutes.Apps)]
[RequireRole("admin")]
public sealed class AppsController(AppService apps, BackendRuntimeOptions runtime) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok(new { data = await apps.ListAsync() });

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id) => Ok(new { data = await apps.GetAsync(id) });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppRequest request) =>
        StatusCode(StatusCodes.Status201Created, new { data = await apps.CreateAsync(request, User.GetUserId()) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] AppRequest request) => Ok(new { data = await apps.UpdateAsync(id, request) });

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await apps.DeleteAsync(id);
        return NoContent();
    }

    /// <summary>A password for one device; the answer carries it once, with the device's addresses.</summary>
    [HttpPost("{id:long}/passwords")]
    public async Task<IActionResult> CreatePassword(long id, [FromBody] AppPasswordRequest request)
    {
        var (password, secret) = await apps.CreatePasswordAsync(id, request);
        var app = await apps.RequireAsync(id);
        var links = AppService.Links(app, secret, PublicUrl.For(Request, runtime));
        return StatusCode(StatusCodes.Status201Created, new { data = new AppPasswordCreated(password, secret, links) });
    }

    [HttpDelete("{id:long}/passwords/{passwordId:long}")]
    public async Task<IActionResult> RevokePassword(long id, long passwordId)
    {
        await apps.RevokePasswordAsync(id, passwordId);
        return NoContent();
    }
}
