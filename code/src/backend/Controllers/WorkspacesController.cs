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
    StoredCustomValues storedValues,
    WorkspaceAccess access) : ControllerBase
{
    /// <summary>The workspaces the caller may open, with their role in each (a key or connection: those it was made for).</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var all = await workspaces.ReloadAsync();
        var userId = User.GetUserId();
        var installationRole = User.GetInstallationRole();
        IReadOnlyList<long>? limited = User.FindFirst("workspaces")?.Value is { } listed
            ? listed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList()
            : null;
        var result = new List<WorkspaceResponse>();
        foreach (var workspace in all)
        {
            var role = userId is { } id && id != 0 ? await access.RoleInAsync(id, installationRole, workspace.Id) : installationRole;
            if (role is not null && WorkspaceAccess.Allows(limited, workspace.Id))
                result.Add(ToResponse(workspace) with { Role = role });
        }
        return Ok(new { data = result });
    }

    // ── Members ──────────────────────────────────────────────────────────

    /// <summary>Who is in a workspace: installation admins (always) and its members; for its admins.</summary>
    [HttpGet("{id:long}/members")]
    public async Task<IActionResult> Members(long id, [FromServices] AuthService auth)
    {
        await RequireWorkspaceAdminAsync(id);
        var users = await auth.ListUsersAsync();
        var admins = users.Where(u => u.Role == "admin")
            .Select(u => new WorkspaceMemberResponse(u.Id, u.Username, "admin", true));
        var members = (await access.MembersAsync(id)).Where(m => m.InstallationRole != "admin")
            .Select(m => new WorkspaceMemberResponse(m.UserId, m.Username, m.Role, false)).ToList();
        // The users who could be added (the installation's admins make new ones).
        var candidates = users.Where(u => u.Role != "admin" && members.All(m => m.UserId != u.Id))
            .Select(u => new { userId = u.Id, username = u.Username, role = u.Role });
        return Ok(new { data = admins.Concat(members).OrderBy(m => m.Username, StringComparer.OrdinalIgnoreCase), candidates });
    }

    /// <summary>Adds a user to the workspace, or changes their role in it.</summary>
    [HttpPut("{id:long}/members/{userId:long}")]
    public async Task<IActionResult> SetMember(long id, long userId, [FromBody] WorkspaceMemberRequest request, [FromServices] AuthService auth)
    {
        await RequireWorkspaceAdminAsync(id);
        await RequireOrdinaryUserAsync(userId, auth);
        await access.SetMemberAsync(id, userId, request.Role);
        return await Members(id, auth);
    }

    [HttpDelete("{id:long}/members/{userId:long}")]
    public async Task<IActionResult> RemoveMember(long id, long userId, [FromServices] AuthService auth)
    {
        await RequireWorkspaceAdminAsync(id);
        await RequireOrdinaryUserAsync(userId, auth);
        await access.RemoveMemberAsync(id, userId);
        return NoContent();
    }

    /// <summary>Installation admins, and the workspace's own admins, manage its members.</summary>
    private async Task RequireWorkspaceAdminAsync(long id)
    {
        if ((await workspaces.ReloadAsync()).All(w => w.Id != id))
            throw new Exceptions.ApiNotFoundException($"Workspace {id} not found.");
        if (User.FindFirst("auth_method") is not null)
            throw new Exceptions.ApiForbiddenException("Members are managed by people signed in to Bento, not by keys or apps.");
        var role = User.GetUserId() is { } userId && userId != 0
            ? await access.RoleInAsync(userId, User.GetInstallationRole(), id)
            : User.GetInstallationRole();
        if (role is null)
            throw new Exceptions.ApiNotFoundException($"Workspace {id} not found.");
        if (role != "admin")
            throw new Exceptions.ApiForbiddenException("Only the workspace's admins manage its members.");
    }

    private static async Task RequireOrdinaryUserAsync(long userId, AuthService auth)
    {
        var user = (await auth.ListUsersAsync()).FirstOrDefault(u => u.Id == userId)
            ?? throw new Exceptions.ApiNotFoundException($"User {userId} not found.");
        if (user.Role == "admin")
            throw new Exceptions.ApiValidationException($"{user.Username} is an installation admin, an admin in every workspace.", new Dictionary<string, object?> { ["field"] = "userId" });
    }

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
        access.Invalidate();
        return NoContent();
    }

    private static WorkspaceResponse ToResponse(Workspace w) => new(w.Id, w.Slug, w.Name, w.IsDefault);
}
