using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Repere.Api.Exceptions;
using Repere.Api.Services;

namespace Repere.Api.Controllers;

[ApiController]
[Route(ApiRoutes.Data)]
public sealed class DataController : ControllerBase
{
    private readonly SchemaCache _schemaCache;

    public DataController(SchemaCache schemaCache)
    {
        _schemaCache = schemaCache;
    }

    /// <summary>
    /// List rows in a table (paginated, filterable, sortable).
    /// </summary>
    [HttpGet("{table}")]
    public IActionResult List(string table)
    {
        var tableDef = ResolveTable(table);
        // CRUD service wired in TASK-036/037
        return Ok(new { data = Array.Empty<object>(), meta = new { page = 1, per_page = 25, total = 0, total_pages = 0 } });
    }

    /// <summary>
    /// Get a single row by ID.
    /// </summary>
    [HttpGet("{table}/{id:int}")]
    public IActionResult GetById(string table, int id)
    {
        var tableDef = ResolveTable(table);
        // CRUD service wired in TASK-036
        return Ok(new { data = (object?)null });
    }

    /// <summary>
    /// Create a new row.
    /// </summary>
    [HttpPost("{table}")]
    public IActionResult Create(string table, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        // CRUD service wired in TASK-036
        return StatusCode(StatusCodes.Status201Created, new { data = (object?)null });
    }

    /// <summary>
    /// Full update of a row (replace all writable columns).
    /// </summary>
    [HttpPut("{table}/{id:int}")]
    public IActionResult Update(string table, int id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        // CRUD service wired in TASK-036
        return Ok(new { data = (object?)null });
    }

    /// <summary>
    /// Partial update of a row (update only provided columns).
    /// </summary>
    [HttpPatch("{table}/{id:int}")]
    public IActionResult Patch(string table, int id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        // CRUD service wired in TASK-036
        return Ok(new { data = (object?)null });
    }

    /// <summary>
    /// Delete a row by ID.
    /// </summary>
    [HttpDelete("{table}/{id:int}")]
    public IActionResult Delete(string table, int id)
    {
        var tableDef = ResolveTable(table);
        // CRUD service wired in TASK-036
        return NoContent();
    }

    /// <summary>
    /// Get related rows for a specific record via a named relation accessor.
    /// </summary>
    [HttpGet("{table}/{id:int}/{relation}")]
    public IActionResult GetRelated(string table, int id, string relation)
    {
        var tableDef = ResolveTable(table);
        // Relation resolution wired in TASK-038
        return Ok(new { data = Array.Empty<object>(), meta = new { page = 1, per_page = 25, total = 0, total_pages = 0 } });
    }

    /// <summary>
    /// Validates that the table exists in the current schema. Returns the table definition
    /// or throws a 404 with the standard error envelope.
    /// </summary>
    private Db.Schema.TableDef ResolveTable(string table)
    {
        return _schemaCache.GetTable(table)
            ?? throw new ApiNotFoundException(
                $"Table '{table}' does not exist.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = table,
                    ["availableTables"] = _schemaCache.GetTableNames()
                });
    }
}
