using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Configuration;
using Bento.Api.Exceptions;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// API key management (Part 3 R3.3, Part 6 R6.6): admins create keys for
/// themselves, see every key without its secret, and revoke keys. Keys
/// cannot manage keys: read and read-write keys never carry the admin role,
/// and keys with permissions only reach what a connected app could.
/// </summary>
[ApiController]
[Route(ApiRoutes.ApiKeys)]
[RequireRole("admin")]
public sealed class ApiKeysController : ControllerBase
{
    private const int MaxNameLength = 120;
    private const int MaxExpiryDays = 3650;

    private readonly ApiKeyService _apiKeys;
    private readonly BackendRuntimeOptions _runtimeOptions;
    private readonly Services.Workspaces.WorkspaceService _workspaces;

    public ApiKeysController(ApiKeyService apiKeys, BackendRuntimeOptions runtimeOptions, Services.Workspaces.WorkspaceService workspaces)
    {
        _apiKeys = apiKeys;
        _runtimeOptions = runtimeOptions;
        _workspaces = workspaces;
    }

    public sealed record CreateApiKeyRequest
    {
        public string? Name { get; init; }
        /// <summary>read or read-write.</summary>
        public string? Scope { get; init; }
        /// <summary>Days until the key stops working; none = no expiry.</summary>
        public int? ExpiresInDays { get; init; }
        /// <summary>Instead of a scope: what the key may do, like a connected app (data.read, schema.write, …).</summary>
        public IReadOnlyList<string>? Permissions { get; init; }
        /// <summary>The workspaces the key may use (Step 5.5); none given: the current one.</summary>
        public IReadOnlyList<long>? Workspaces { get; init; }
        /// <summary>The key may use every workspace, also ones made later.</summary>
        public bool AllWorkspaces { get; init; }
    }

    public sealed record ApiKeyResponse(
        long Id,
        string Name,
        string? Prefix,
        string Scope,
        string? CreatedBy,
        string CreatedAt,
        string? ExpiresAt,
        string? LastUsedAt,
        string? RevokedAt,
        string Status,
        IReadOnlyList<string>? Permissions = null,
        IReadOnlyList<long>? Workspaces = null);

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(new { data = (await _apiKeys.ListAsync()).Select(ToResponse) });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request)
    {
        if (!_runtimeOptions.IsStandardAuthMode)
            throw new ApiValidationException("API keys need sign-in to be switched on (AUTH_MODE=standard).");
        var userId = HttpContext.User.GetUserId()
            ?? throw new ApiUnauthorizedException("Authentication is required.");

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            throw Invalid("name", "Give the key a name, e.g. the script or service that uses it.");
        if (name.Length > MaxNameLength)
            throw Invalid("name", "The name is too long.");
        if (request.ExpiresInDays is < 1 or > MaxExpiryDays)
            throw Invalid("expiresInDays", $"Expiry must be between 1 and {MaxExpiryDays} days.");

        IReadOnlyList<string>? permissions = null;
        if (request.Permissions is { } chosen)
        {
            var unknown = chosen.FirstOrDefault(p => !McpPermissions.All.Contains(p));
            if (unknown is not null)
                throw Invalid("permissions", $"Unknown permission '{unknown}'; use {string.Join(", ", McpPermissions.All)}.");
            permissions = McpPermissions.Normalize(chosen);
        }
        var scope = permissions is null ? ApiKeyService.NormalizeScope(request.Scope) : ApiKeyService.ReadWriteScope;
        IReadOnlyList<long>? workspaces = null;
        if (!request.AllWorkspaces)
        {
            workspaces = request.Workspaces is { Count: > 0 } listed ? listed.Distinct().ToList() : [Services.Workspaces.WorkspaceContext.Id];
            var known = await _workspaces.ReloadAsync();
            var missing = workspaces.FirstOrDefault(id => known.All(w => w.Id != id));
            if (missing != 0)
                throw Invalid("workspaces", $"Workspace {missing} not found.");
        }
        var (row, key) = await _apiKeys.CreateAsync(name, scope, request.ExpiresInDays, userId, permissions, workspaces);
        // The only time the key is ever sent.
        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(row), key });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Revoke(long id)
    {
        await _apiKeys.RevokeAsync(id);
        return NoContent();
    }

    private static ApiKeyResponse ToResponse(ApiKeyService.ApiKeyRow row)
    {
        var status = row.RevokedAt is not null
            ? "revoked"
            : row.ExpiresAt is not null && DateTimeOffset.Parse(row.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow
                ? "expired"
                : "active";
        return new ApiKeyResponse(row.Id, row.Name, row.Prefix, row.Permissions is null ? row.Scope : "permissions", row.Username, row.CreatedAt, row.ExpiresAt, row.LastUsedAt, row.RevokedAt, status, row.Permissions, row.Workspaces);
    }

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
