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
    public async Task<IActionResult> Get(long id) => Ok(new { data = await apps.GetAsync(id), setupLink = SetupLink(id) });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppRequest request)
    {
        var app = await apps.CreateAsync(request, User.GetUserId());
        return StatusCode(StatusCodes.Status201Created, new { data = app, setupLink = SetupLink(app.Id) });
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] AppRequest request) =>
        Ok(new { data = await apps.UpdateAsync(id, request), setupLink = SetupLink(id) });

    /// <summary>
    /// The app's page in Bento, opened at "Add device" (Step 5.14): a connection sets the
    /// app up, the user makes the device's password there — it never passes through the chat.
    /// </summary>
    private string SetupLink(long id)
    {
        var workspace = Services.Workspaces.WorkspaceContext.Current;
        var prefix = workspace.IsDefault ? "" : $"/w/{workspace.Slug}";
        return $"{PublicUrl.For(Request, runtime).TrimEnd('/')}{prefix}/apps?app={id}";
    }

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
