using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Exceptions;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>Tables made from saved queries (Step 5.8): when they were filled, and filling one now.</summary>
[ApiController]
[Route(ApiRoutes.Prefix + "/query-tables")]
[RequireRole("viewer", "api")]
public sealed class QueryTablesController(QueryTables tables, SchemaCache schema) : ControllerBase
{
    [HttpGet("{table}")]
    public IActionResult Status(string table)
    {
        var def = schema.GetTable(table) ?? throw new ApiNotFoundException($"Table '{table}' does not exist.");
        if (CrudService.SourceQueryOf(def) is null)
            throw new ApiNotFoundException($"'{def.Name}' is not made from a saved query.");
        return Ok(new { data = tables.StatusOf(def) });
    }

    [HttpPost("{table}/refresh")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Refresh(string table) => Ok(new { data = await tables.RefreshAsync(table) });
}
