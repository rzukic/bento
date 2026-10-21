using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Models;
using Bento.Api.Services;
using Bento.Api.Services.Workspaces;

namespace Bento.Api.Controllers;

/// <summary>
/// The installation's users (Step 5.5 part B), for its admins: who exists, their
/// installation role, the workspaces they are in. A new user joins the
/// workspaces chosen for them; a reset password signs them out everywhere.
/// </summary>
[ApiController]
[Route(ApiRoutes.Admin + "/users")]
[RequireRole("admin")]
public sealed class UsersController(AuthService auth, WorkspaceService workspaces, WorkspaceAccess access) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var all = await workspaces.ReloadAsync();
        var users = await auth.ListUsersAsync();
        var result = new List<UserResponse>();
        foreach (var user in users)
            result.Add(await ToResponseAsync(user, all));
        return Ok(new { data = result });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserCreateRequest request)
    {
        var role = (request.Role ?? "viewer").Trim().ToLowerInvariant();
        var known = await workspaces.ReloadAsync();
        // Checked before the user exists: a wrong workspace or role leaves nothing behind.
        var joins = role == "admin"
            ? []
            : (request.Workspaces is { Count: > 0 } chosen ? chosen.Select(w => (w.Id, Role: w.Role ?? role)).ToList() : [(WorkspaceContext.Id, Role: role)])
                .Select(j => (j.Id, Role: WorkspaceAccess.NormalizeRole(j.Role))).ToList();
        foreach (var (workspaceId, _) in joins)
        {
            if (known.All(w => w.Id != workspaceId))
                throw new Exceptions.ApiValidationException($"Workspace {workspaceId} not found.", new Dictionary<string, object?> { ["field"] = "workspaces" });
        }
        var id = await auth.CreateUserAsync(request.Username, request.Password, request.Role);
        foreach (var (workspaceId, workspaceRole) in joins)
            await access.SetMemberAsync(workspaceId, id, workspaceRole);
        var user = (await auth.ListUsersAsync()).Single(u => u.Id == id);
        return StatusCode(StatusCodes.Status201Created, new { data = await ToResponseAsync(user, await workspaces.ReloadAsync()) });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UserUpdateRequest request)
    {
        await auth.UpdateUserAsync(id, request.Role, string.IsNullOrEmpty(request.Password) ? null : request.Password);
        access.Invalidate();
        var user = (await auth.ListUsersAsync()).Single(u => u.Id == id);
        return Ok(new { data = await ToResponseAsync(user, await workspaces.ReloadAsync()) });
    }

    private async Task<UserResponse> ToResponseAsync(AuthService.UserRow user, IReadOnlyList<Workspace> all)
    {
        var roles = user.Role == "admin"
            ? all.ToDictionary(w => w.Id, _ => "admin")
            : await access.RolesOfAsync(user.Id);
        var joined = all.Where(w => roles.ContainsKey(w.Id)).Select(w => new UserWorkspaceRole(w.Id, w.Slug, w.Name, roles[w.Id])).ToList();
        return new UserResponse(user.Id, user.Username, user.Role, user.CreatedAt, joined);
    }
}
