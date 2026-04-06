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
    private readonly CrudService _crudService;

    public DataController(SchemaCache schemaCache, CrudService crudService)
    {
        _schemaCache = schemaCache;
        _crudService = crudService;
    }

    /// <summary>
    /// List rows in a table (paginated, filterable, sortable).
    /// </summary>
    [HttpGet("{table}")]
    public async Task<IActionResult> List(string table)
    {
        var tableDef = ResolveTable(table);
        var parameters = QueryParameterParser.Parse(Request.Query, tableDef);
        var result = await _crudService.ListAsync(tableDef, parameters);

        return Ok(new
        {
            data = result.Data,
            meta = new
            {
                page = result.Page,
                per_page = result.PerPage,
                total = result.Total,
                total_pages = result.TotalPages
            }
        });
    }

    /// <summary>
    /// Get a single row by ID.
    /// </summary>
    [HttpGet("{table}/{id:long}")]
    public async Task<IActionResult> GetById(string table, long id)
    {
        var tableDef = ResolveTable(table);
        var row = await _crudService.GetByIdAsync(tableDef, id);
        return Ok(new { data = row });
    }

    /// <summary>
    /// Create a new row.
    /// </summary>
    [HttpPost("{table}")]
    public async Task<IActionResult> Create(string table, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        var row = await _crudService.CreateAsync(tableDef, body);
        return StatusCode(StatusCodes.Status201Created, new { data = row });
    }

    /// <summary>
    /// Full update of a row (replace all writable columns).
    /// </summary>
    [HttpPut("{table}/{id:long}")]
    public async Task<IActionResult> Update(string table, long id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        var row = await _crudService.UpdateAsync(tableDef, id, body);
        return Ok(new { data = row });
    }

    /// <summary>
    /// Partial update of a row (update only provided columns).
    /// </summary>
    [HttpPatch("{table}/{id:long}")]
    public async Task<IActionResult> Patch(string table, long id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        var row = await _crudService.PatchAsync(tableDef, id, body);
        return Ok(new { data = row });
    }

    /// <summary>
    /// Delete a row by ID.
    /// </summary>
    [HttpDelete("{table}/{id:long}")]
    public async Task<IActionResult> Delete(string table, long id)
    {
        var tableDef = ResolveTable(table);
        await _crudService.DeleteAsync(tableDef, id);
        return NoContent();
    }

    /// <summary>
    /// Get related rows for a specific record via a named relation accessor.
    /// </summary>
    [HttpGet("{table}/{id:long}/{relation}")]
    public IActionResult GetRelated(string table, long id, string relation)
    {
        var tableDef = ResolveTable(table);
        // Relation resolution wired in TASK-038
        return Ok(new { data = Array.Empty<object>(), meta = new { page = 1, per_page = 25, total = 0, total_pages = 0 } });
    }

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
