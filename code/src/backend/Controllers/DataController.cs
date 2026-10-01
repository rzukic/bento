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
    private readonly IncludeService _includeService;

    public DataController(SchemaCache schemaCache, CrudService crudService, IncludeService includeService)
    {
        _schemaCache = schemaCache;
        _crudService = crudService;
        _includeService = includeService;
    }

    /// <summary>
    /// List rows in a table (paginated, filterable, sortable).
    /// </summary>
    [HttpGet("{table}")]
    public async Task<IActionResult> List(string table)
    {
        var tableDef = ResolveTable(table);
        var parameters = QueryParameterParser.Parse(Request.Query, tableDef);
        var includes = parameters.Includes.Count > 0
            ? _includeService.ResolveIncludes(tableDef.Name, parameters.Includes)
            : null;
        var requiredIncludeColumns = includes is not null
            ? IncludeService.GetRequiredLinkColumns(includes)
            : null;
        var result = await _crudService.ListAsync(tableDef, parameters, requiredIncludeColumns);

        // Apply relation includes
        if (includes is not null)
        {
            var mutableRows = result.Data.ToList();
            await _includeService.ApplyIncludesAsync(mutableRows, includes);
            TrimRowsToRequestedFields(mutableRows, parameters, includes);
            return Ok(new
            {
                data = mutableRows,
                meta = new { page = result.Page, per_page = result.PerPage, total = result.Total, total_pages = result.TotalPages }
            });
        }

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
    [HttpGet("{table}/{id}")]
    public async Task<IActionResult> GetById(string table, string id)
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
    [HttpPut("{table}/{id}")]
    public async Task<IActionResult> Update(string table, string id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        var row = await _crudService.UpdateAsync(tableDef, id, body);
        return Ok(new { data = row });
    }

    /// <summary>
    /// Partial update of a row (update only provided columns).
    /// </summary>
    [HttpPatch("{table}/{id}")]
    public async Task<IActionResult> Patch(string table, string id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        var row = await _crudService.PatchAsync(tableDef, id, body);
        return Ok(new { data = row });
    }

    /// <summary>
    /// Delete a row by ID.
    /// </summary>
    [HttpDelete("{table}/{id}")]
    public async Task<IActionResult> Delete(string table, string id)
    {
        var tableDef = ResolveTable(table);
        await _crudService.DeleteAsync(tableDef, id);
        return NoContent();
    }

    /// <summary>
    /// Get related rows for a specific record via a named relation accessor.
    /// </summary>
    [HttpGet("{table}/{id}/{relation}")]
    public async Task<IActionResult> GetRelated(string table, string id, string relation)
    {
        var tableDef = ResolveTable(table);
        var result = await _includeService.LoadRelatedRowsAsync(tableDef.Name, id, relation, Request.Query);

        return Ok(new
        {
            data = result.Rows,
            meta = new
            {
                page = result.Page,
                per_page = result.PerPage,
                total = result.Total,
                total_pages = result.TotalPages
            }
        });
    }

    private static void TrimRowsToRequestedFields(
        IReadOnlyList<IDictionary<string, object?>> rows,
        ListQueryParameters parameters,
        IReadOnlyList<ResolvedInclude> includes)
    {
        if (parameters.Fields is not { Count: > 0 })
            return;

        var allowedKeys = new HashSet<string>(parameters.Fields, StringComparer.OrdinalIgnoreCase);
        foreach (var include in includes)
        {
            allowedKeys.Add(include.AccessorName);
        }

        foreach (var row in rows)
        {
            var keysToRemove = row.Keys
                .Where(key => !allowedKeys.Contains(key))
                .ToList();

            foreach (var key in keysToRemove)
            {
                row.Remove(key);
            }
        }
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
