using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Configuration;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Services;
using Bento.Api.Services.Mcp;
using Bento.Api.Services.Sharing;
using Dapper;

namespace Bento.Api.Controllers;

/// <summary>
/// Share links (Step 5.6): the dashboard's owner (or the workspace's admins)
/// makes links, sees who opened them how often, and revokes them. A link's
/// visitor asks <c>GET /share</c> for what it shows.
/// </summary>
[ApiController]
public sealed class SharesController(ShareService shares, ISystemPool systemPool, BackendRuntimeOptions runtime) : ControllerBase
{
    public sealed record ShareRequest
    {
        public string? Name { get; init; }
        public int? ExpiresInDays { get; init; }
        /// <summary>Fixed filter values by column (the dashboard's filters).</summary>
        public Dictionary<string, List<JsonElement>>? Filters { get; init; }
        public bool Embed { get; init; }
        /// <summary>A form: entries one visitor may send an hour (default 10).</summary>
        public int? PerHour { get; init; }
        /// <summary>A form: what the visitor reads after sending.</summary>
        public string? Thanks { get; init; }
    }

    public sealed record ShareResponse(long Id, string Name, string Prefix, string Status, string? CreatedBy, string CreatedAt, string? ExpiresAt,
        string? LastUsedAt, int Views, Dictionary<string, List<JsonElement>>? Filters, bool Embed, int? PerHour = null, string? Thanks = null);

    [HttpGet(ApiRoutes.Dashboards + "/{id:long}/shares")]
    [RequireRole("viewer")]
    public async Task<IActionResult> List(long id)
    {
        await RequireDashboardAsync(id);
        return Ok(new { data = (await shares.ListAsync("dashboard", id)).Select(ToResponse) });
    }

    /// <summary>A new link; its address comes back once.</summary>
    [HttpPost(ApiRoutes.Dashboards + "/{id:long}/shares")]
    [RequireRole("viewer")]
    public async Task<IActionResult> Create(long id, [FromBody] ShareRequest request)
    {
        var dashboard = await RequireDashboardAsync(id);
        var declared = Declared(dashboard);
        var filters = (request.Filters ?? []).Where(f => f.Value is { Count: > 0 }).ToDictionary(f => f.Key, f => f.Value);
        var unknown = filters.Keys.FirstOrDefault(column => !declared.Contains(column));
        if (unknown is not null)
            throw new ApiValidationException($"'{unknown}' is not a filter of this dashboard.", new Dictionary<string, object?> { ["field"] = "filters" });
        var (link, token) = await shares.CreateAsync("dashboard", id, request.Name, request.ExpiresInDays,
            new ShareService.ShareOptions(filters.Count == 0 ? null : filters, request.Embed), User.GetUserId()!.Value);
        var url = $"{PublicUrl.For(Request, runtime)}/s/{token}";
        return StatusCode(StatusCodes.Status201Created, new
        {
            data = ToResponse(link),
            token,
            url,
            embed = request.Embed ? $"<iframe src=\"{url}?embed=1\" width=\"100%\" height=\"600\" style=\"border:0\" loading=\"lazy\"></iframe>" : null
        });
    }

    // ── Forms (part B): a form for people without an account ─────────────

    [HttpGet(ApiRoutes.Forms + "/{id:long}/shares")]
    [RequireRole("viewer")]
    public async Task<IActionResult> ListForForm(long id)
    {
        await RequireFormAsync(id);
        return Ok(new { data = (await shares.ListAsync("form", id)).Select(ToResponse) });
    }

    [HttpPost(ApiRoutes.Forms + "/{id:long}/shares")]
    [RequireRole("viewer")]
    public async Task<IActionResult> CreateForForm(long id, [FromBody] ShareRequest request)
    {
        await RequireFormAsync(id);
        var (link, token) = await shares.CreateAsync("form", id, request.Name, request.ExpiresInDays,
            new ShareService.ShareOptions(null, request.Embed, request.PerHour ?? ShareService.DefaultPerHour, string.IsNullOrWhiteSpace(request.Thanks) ? null : request.Thanks.Trim()),
            User.GetUserId()!.Value);
        return StatusCode(StatusCodes.Status201Created, new { data = ToResponse(link), token, url = LinkUrl(token) });
    }

    [HttpDelete(ApiRoutes.Prefix + "/shares/{id:long}")]
    [RequireRole("viewer")]
    public async Task<IActionResult> Revoke(long id)
    {
        var link = await shares.GetAsync(id);
        if (link.Kind == "form")
            await RequireFormAsync(link.TargetId);
        else
            await RequireDashboardAsync(link.TargetId);
        await shares.RevokeAsync(id);
        return NoContent();
    }

    /// <summary>For a link's visitor: the dashboard, its fixed filters, whether it may be embedded, and how numbers and dates read.</summary>
    [HttpGet(ApiRoutes.Prefix + "/share")]
    public async Task<IActionResult> Info([FromServices] ShareGuard guard, [FromServices] WorkspaceSettings settings)
    {
        var visitor = HttpContext.ShareVisitor() ?? throw new ApiNotFoundException("Open this with a share link.");
        if (visitor.Link.Kind == "form")
            await guard.CheckFormAsync(visitor, visitor.Link.TargetId);
        else
            await guard.CheckDashboardAsync(visitor, visitor.Link.TargetId);
        var format = await settings.GetAsync();
        return Ok(new
        {
            data = new
            {
                kind = visitor.Link.Kind,
                dashboardId = visitor.Link.Kind == "dashboard" ? visitor.Link.TargetId : (long?)null,
                formId = visitor.Link.Kind == "form" ? visitor.Link.TargetId : (long?)null,
                thanks = visitor.Link.Options.Thanks,
                name = visitor.Link.Name,
                filters = visitor.Link.Options.Filters,
                embed = visitor.Link.Options.Embed,
                expiresAt = visitor.Link.ExpiresAt,
                settings = new
                {
                    locale = format.Locale,
                    currency = format.Currency,
                    timeZone = format.Calendar.ZoneId,
                    weekStart = format.WeekStart,
                    yearStart = format.YearStart
                }
            }
        });
    }

    /// <summary>
    /// People signed in, and apps connected with "Share links" (Step 5.14; the
    /// permission is checked before) — never API keys or a link's visitors.
    /// </summary>
    /// <summary>The link to hand out (Step 5.14: a connected app gets it whole); shown once, like the token.</summary>
    private string LinkUrl(string token) => $"{Services.Mcp.PublicUrl.For(Request, runtime).TrimEnd('/')}/s/{token}";

    private void EnsureMayShare()
    {
        if (User.FindFirstValue("auth_method") is { } method && method != "oauth")
            throw new ApiForbiddenException("Share links are made by people signed in to Bento or apps connected with \"Share links\".");
    }

    /// <summary>The dashboard's owner and the workspace's admins share it; keys and links cannot.</summary>
    private async Task<JsonElement> RequireDashboardAsync(long id)
    {
        EnsureMayShare();
        var table = systemPool.Provider == DatabaseProvider.Postgres ? "_system.dashboards" : "dashboards";
        using var conn = await systemPool.GetConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(long? Owner, string Json)?>(
            $"SELECT owner_user_id, CAST(dashboard_json AS TEXT) FROM {table} WHERE id = @Id AND workspace_id = @Ws",
            new { Id = id, Ws = Services.Workspaces.WorkspaceContext.Id });
        if (row is not { } found)
            throw new ApiNotFoundException($"Dashboard {id} not found.");
        if (found.Owner != User.GetUserId() && User.GetRole() != "admin")
            throw new ApiForbiddenException("Only the dashboard's owner and the workspace's admins share it.");
        return JsonDocument.Parse(found.Json).RootElement.Clone();
    }

    /// <summary>The form's owner and the workspace's admins share it.</summary>
    private async Task RequireFormAsync(long id)
    {
        EnsureMayShare();
        var table = systemPool.Provider == DatabaseProvider.Postgres ? "_system.forms" : "forms";
        using var conn = await systemPool.GetConnectionAsync();
        var owner = await conn.QuerySingleOrDefaultAsync<long?>(
            $"SELECT owner_user_id FROM {table} WHERE id = @Id AND workspace_id = @Ws", new { Id = id, Ws = Services.Workspaces.WorkspaceContext.Id });
        if (owner is null && await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE id = @Id AND workspace_id = @Ws",
                new { Id = id, Ws = Services.Workspaces.WorkspaceContext.Id }) == 0)
            throw new ApiNotFoundException($"Form {id} not found.");
        if (owner != User.GetUserId() && User.GetRole() != "admin")
            throw new ApiForbiddenException("Only the form's owner and the workspace's admins share it.");
    }

    private static HashSet<string> Declared(JsonElement dashboard) =>
        dashboard.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array
            ? filters.EnumerateArray().Where(f => f.TryGetProperty("column", out _)).Select(f => f.GetProperty("column").GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private static ShareResponse ToResponse(ShareService.ShareLink link) =>
        new(link.Id, link.Name, link.Prefix, link.Status, link.CreatedByName, link.CreatedAt, link.ExpiresAt, link.LastUsedAt, link.Views, link.Options.Filters, link.Options.Embed,
            link.Options.PerHour, link.Options.Thanks);
}
