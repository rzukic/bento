namespace Bento.Api.Models;

// ── Workspaces: separate areas of one installation (Step 5.5) ────────────

public sealed record WorkspaceResponse(long Id, string Slug, string Name, bool IsDefault);

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
