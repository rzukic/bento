namespace Bento.Api.Models;

// ── Workspaces: separate areas of one installation (Step 5.5) ────────────

/// <param name="Role">The caller's role in it: admin, editor, viewer or api.</param>
public sealed record WorkspaceResponse(long Id, string Slug, string Name, bool IsDefault, string? Role = null);

// ── Members and users (Step 5.5 part B) ──────────────────────────────────

/// <param name="InstallationAdmin">Admins of the installation are admins in every workspace; their row cannot be changed here.</param>
public sealed record WorkspaceMemberResponse(long UserId, string Username, string Role, bool InstallationAdmin);

public sealed record WorkspaceMemberRequest
{
    public string? Role { get; init; }
}

public sealed record UserWorkspaceRole(long Id, string Slug, string Name, string Role);

/// <param name="Role">The installation role: admin (every workspace, users, vault …) or the role a member gets by default.</param>
public sealed record UserResponse(int Id, string Username, string Role, string CreatedAt, IReadOnlyList<UserWorkspaceRole> Workspaces);

public sealed record UserWorkspaceRequest
{
    public long Id { get; init; }
    public string? Role { get; init; }
}

public sealed record UserCreateRequest
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Role { get; init; }

    /// <summary>The workspaces the user joins and their role there; without any, the current workspace with <see cref="Role"/>.</summary>
    public List<UserWorkspaceRequest>? Workspaces { get; init; }
}

public sealed record UserUpdateRequest
{
    public string? Role { get; init; }
    public string? Password { get; init; }
}

public sealed record WorkspaceRequest
{
    public string? Name { get; init; }

    /// <summary>The short name in addresses (/w/&lt;slug&gt;, /mcp/&lt;slug&gt;); made from the name when left out. Kept for life.</summary>
    public string? Slug { get; init; }
}

public sealed record WorkspaceDeleteRequest
{
    /// <summary>The workspace's short name, typed again.</summary>
    public string? Confirm { get; init; }
}
