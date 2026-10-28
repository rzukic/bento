using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// The templates installed in this workspace: the app notes one after
/// installing it, so the AI and MCP clients learn its tables and conventions
/// here — and nowhere else.
/// </summary>
[ApiController]
[Route(ApiRoutes.Prefix + "/templates")]
[RequireRole("viewer", "api")]
public sealed class TemplatesController(TemplateRegistry templates) : ControllerBase
{
    public sealed record TemplateRequest
    {
        public string? Title { get; init; }
        public Dictionary<string, string>? Tables { get; init; }
    }

    [HttpGet]
    public async Task<IActionResult> List() => Ok(new { data = await templates.ListAsync() });

    /// <summary>The templates Bento can install (Step 5.15), with what each sets up.</summary>
    [HttpGet("catalog")]
    public IActionResult Catalog([FromServices] TemplateCatalog catalog) => Ok(new { data = catalog.List() });

    public sealed record InstallRequest
    {
        /// <summary>Put before every table name, when one is taken (e.g. work_).</summary>
        public string? Prefix { get; init; }
        /// <summary>Example rows to try it out (default: yes).</summary>
        public bool? Examples { get; init; }
    }

    /// <summary>
    /// Installs a template (Step 5.15): its tables, example rows, dashboards and
    /// forms, as the caller — each step through the API with their rights. A
    /// connection or key needs every permission the steps do; it is told which are missing first.
    /// </summary>
    [HttpPost("{id}/install")]
    [RequireRole("admin")]
    public async Task<IActionResult> Install(string id, [FromBody] InstallRequest request, [FromServices] TemplateInstaller installer)
    {
        var examples = request.Examples ?? true;
        var granted = User.FindFirstValue("oauth_permissions") ?? User.FindFirstValue("api_key_permissions");
        if (granted is not null)
        {
            var have = granted.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var missing = installer.Needs(id, examples).Where(p => !have.Contains(p)).ToList();
            if (missing.Count > 0)
                throw new Exceptions.ApiForbiddenException($"Installing this template also needs {string.Join(", ", missing.Select(m => $"\"{m}\""))}; reconnect and tick them.",
                    new Dictionary<string, object?> { ["permissions"] = missing });
        }
        var credentials = Request.Headers.Authorization.Count > 0
            ? new Services.Mcp.BentoSelfClient.Credentials("Authorization", Request.Headers.Authorization.ToString())
            : Request.Headers.ContainsKey("X-API-Key")
                ? new Services.Mcp.BentoSelfClient.Credentials("X-API-Key", Request.Headers["X-API-Key"].ToString())
                : new Services.Mcp.BentoSelfClient.Credentials(null, null);
        var result = await installer.InstallAsync(id, request.Prefix, examples, credentials, HttpContext.RequestAborted);
        return Ok(new { data = result });
    }

    [HttpPut("{id}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Record(string id, [FromBody] TemplateRequest request) =>
        Ok(new { data = await templates.RecordAsync(id, request.Title, request.Tables) });

    [HttpDelete("{id}")]
    [RequireRole("admin")]
    public async Task<IActionResult> Remove(string id)
    {
        await templates.RemoveAsync(id);
        return NoContent();
    }
}
