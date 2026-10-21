using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Models;
using Bento.Api.Services;
using Bento.Api.Services.Workspaces;
using Bento.Api.Workers;

namespace Bento.Api.Controllers;

/// <summary>
/// The workspaces of the installation (Step 5.5): everyone signed in sees
/// them (the switcher); admins make, rename and delete them. A new workspace
/// is ready at once — its tables' store, an empty model, no functions.
/// </summary>
[ApiController]
[Route(ApiRoutes.Workspaces)]
public sealed class WorkspacesController(
    WorkspaceService workspaces,
    SchemaCache schemaCache,
    CustomFunctionService functions,
    StoredCustomValues storedValues) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(new { data = (await workspaces.ReloadAsync()).Select(ToResponse) });

    [HttpPost]
    [RequireRole("admin")]
    public async Task<IActionResult> Create([FromBody] WorkspaceRequest request)
    {
        var workspace = await workspaces.CreateAsync(request.Name, request.Slug);
        using (WorkspaceContext.Use(workspace))
        {
            await schemaCache.ReloadAsync();
            await functions.ReloadAsync();
            await storedValues.EnsureTableAsync();
        }
        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(workspace) });
    }

    [HttpPut("{id:long}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Rename(long id, [FromBody] WorkspaceRequest request)
    {
        if (request.Slug is not null && workspaces.Find(id) is { } existing && !string.Equals(request.Slug.Trim(), existing.Slug, StringComparison.OrdinalIgnoreCase))
            throw new Exceptions.ApiValidationException("A workspace keeps its short name: addresses, keys and devices use it.",
                new Dictionary<string, object?> { ["field"] = "slug" });
        return Ok(new { data = ToResponse(await workspaces.RenameAsync(id, request.Name)) });
    }

    /// <summary>Deletes the workspace with its tables, dashboards, forms, imports, rules, apps and settings.</summary>
    [HttpDelete("{id:long}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Delete(long id, [FromBody] WorkspaceDeleteRequest? request, [FromQuery] string? confirm = null)
    {
        await workspaces.DeleteAsync(id, request?.Confirm ?? confirm);
        return NoContent();
    }

    private static WorkspaceResponse ToResponse(Workspace w) => new(w.Id, w.Slug, w.Name, w.IsDefault);
}
