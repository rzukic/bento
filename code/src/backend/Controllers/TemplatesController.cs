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
