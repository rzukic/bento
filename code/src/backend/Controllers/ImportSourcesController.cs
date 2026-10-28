using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Models;
using Bento.Api.Services.Import;

namespace Bento.Api.Controllers;

/// <summary>
/// Saved imports from a URL (Step 5.1): list, create, change, delete, run now
/// and their run log. Like importing, every route needs <c>editor</c> or above
/// (or <c>api</c>); credentials from the vault in a URL or header need <c>admin</c>.
/// </summary>
[ApiController]
[Route(ApiRoutes.Import + "/{table}/sources")]
[RequireRole("editor", "api")]
public sealed class ImportSourcesController(ImportSourceService sources) : ControllerBase
{
    // The vault belongs to the installation: a workspace admin cannot put its secrets in a request.
    private bool Admin => User.IsInstallationAdmin();

    [HttpGet]
    public async Task<IActionResult> List(string table) => Ok(new { data = await sources.ListAsync(table) });

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(string table, long id) => Ok(new { data = await sources.GetAsync(table, id) });

    [HttpPost]
    public async Task<IActionResult> Create(string table, [FromBody] ImportSourceRequest request) =>
        StatusCode(StatusCodes.Status201Created, new { data = await sources.CreateAsync(table, request, Admin, User.GetUserId()) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(string table, long id, [FromBody] ImportSourceRequest request) =>
        Ok(new { data = await sources.UpdateAsync(table, id, request, Admin) });

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(string table, long id)
    {
        await sources.DeleteAsync(table, id, Admin);
        return NoContent();
    }

    /// <summary>Fetches and imports now; the answer is the run (also when it failed: see its error).</summary>
    [HttpPost("{id:long}/run")]
    public async Task<IActionResult> Run(string table, long id) =>
        Ok(new { data = await sources.RunAsync(table, id, "manual", HttpContext.RequestAborted) });

    [HttpGet("{id:long}/runs")]
    public async Task<IActionResult> Runs(string table, long id, [FromQuery] int limit = 20) =>
        Ok(new { data = await sources.RunsAsync(table, id, limit) });
}
