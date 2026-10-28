using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Authorization;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Services;

namespace Bento.Api.Controllers;

/// <summary>
/// Auto-generated row CRUD. Reads are open to every authenticated role
/// (<c>viewer</c> and above, plus <c>api</c>); writes additionally require
/// <c>editor</c> or above, or the <c>api</c> role.
/// </summary>
[ApiController]
[Route(ApiRoutes.Data)]
[RequireRole("viewer", "api")]
public sealed class DataController : ControllerBase
{
    private readonly SchemaCache _schemaCache;
    private readonly CrudService _crudService;
    private readonly IncludeService _includeService;
    private readonly ICustomValueRecalculation? _recalculation;

    public DataController(SchemaCache schemaCache, CrudService crudService, IncludeService includeService, ICustomValueRecalculation? recalculation = null)
    {
        _schemaCache = schemaCache;
        _crudService = crudService;
        _includeService = includeService;
        _recalculation = recalculation;
    }

    /// <summary>
    /// List rows in a table (paginated, filterable, sortable).
    /// </summary>
    [HttpGet("{table}")]
    public async Task<IActionResult> List(string table, [FromServices] Services.Sharing.ShareGuard guard, [FromServices] QueryTables queryTables)
    {
        var tableDef = ResolveTable(table);
        await queryTables.EnsureCurrentAsync(tableDef.Name);
        var parameters = QueryParameterParser.Parse(Request.Query, tableDef);
        // A share link's visitor reads only the dashboard's tables of rows and targets (Step 5.6).
        if (Services.Sharing.ShareContext.ShareVisitor(HttpContext) is { } visitor)
            parameters = await guard.CheckRowsAsync(visitor, tableDef, parameters);
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
    /// Many rows in one transaction (up to 1000):
    /// <c>{ "rows": [ {…}, … ], "upsertOn": ["external_id"] }</c>. With upsertOn,
    /// a row matching an existing one on those columns updates it (only the
    /// given columns); the others are inserted. Also for links in bulk:
    /// <c>{ "rows": [ { "external_id": "A1", "category_id": 4 } ], "upsertOn": ["external_id"] }</c>.
    /// </summary>
    [HttpPost("{table}/bulk")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Bulk(string table, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new ApiValidationException("Send { \"rows\": [ … ], \"upsertOn\": [\"column\"] } (upsertOn is optional).", new Dictionary<string, object?> { ["field"] = "rows" });
        var upsertOn = body.TryGetProperty("upsertOn", out var keys) && keys.ValueKind == JsonValueKind.Array
            ? keys.EnumerateArray().Select(k => k.GetString() ?? "").ToList()
            : [];
        var results = await _crudService.BulkAsync(tableDef, rows.EnumerateArray().ToList(), upsertOn);
        _recalculation?.Request();
        return Ok(new
        {
            data = new
            {
                inserted = results.Count(r => r.Action == "inserted"),
                updated = results.Count(r => r.Action == "updated"),
                unchanged = results.Count(r => r.Action == "unchanged"),
                rows = results.Select(r => new { index = r.Index, id = r.Id, action = r.Action })
            }
        });
    }

    /// <summary>
    /// Create a new row.
    /// </summary>
    [HttpPost("{table}")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Create(string table, [FromBody] JsonElement body, [FromServices] Services.Sharing.ShareGuard guard,
        [FromServices] Services.Sharing.ShareService shares)
    {
        var tableDef = ResolveTable(table);
        // A public form's visitor (Step 5.6): only the form's fields, a few entries an hour, no robots.
        if (Services.Sharing.ShareContext.ShareVisitor(HttpContext) is { } visitor)
        {
            body = await guard.CheckEntryAsync(visitor, tableDef, body);
            RefuseRobots();
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!shares.TakeEntry(visitor.Link, ip, DateTime.UtcNow))
                throw new ApiTooManyRequestsException("Thanks — that is enough entries for now. Try again later.");
            var created = await _crudService.CreateAsync(tableDef, body);
            _recalculation?.Request();
            // The visitor learns that it arrived, not what the table computes from it.
            var key = tableDef.Columns.FirstOrDefault(c => c.PrimaryKey)?.Name ?? "id";
            return StatusCode(StatusCodes.Status201Created, new { data = new Dictionary<string, object?> { [key] = created.TryGetValue(key, out var id) ? id : null } });
        }
        var row = await FreshAsync(tableDef, await _crudService.CreateAsync(tableDef, body));
        return StatusCode(StatusCodes.Status201Created, new { data = row });
    }

    /// <summary>
    /// The form's page sends its trap field as "ok:&lt;what was typed&gt;" — people
    /// never see it, robots fill it — and how long the form was open; a few
    /// seconds is a person.
    /// </summary>
    private void RefuseRobots()
    {
        var trap = Request.Headers["X-Bento-Form-Check"];
        var opened = Request.Headers["X-Bento-Form-Elapsed"].ToString();
        var seconds = double.TryParse(opened, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
        if (trap.ToString() != "ok:" || seconds < 2)
            throw new ApiValidationException("This entry looks automatic. Fill in the form on its page and send it again.",
                new Dictionary<string, object?> { ["field"] = "form" });
    }

    /// <summary>
    /// Full update of a row (replace all writable columns).
    /// </summary>
    [HttpPut("{table}/{id}")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Update(string table, string id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        var row = await FreshAsync(tableDef, await _crudService.UpdateAsync(tableDef, id, body));
        return Ok(new { data = row });
    }

    /// <summary>
    /// Partial update of a row (update only provided columns).
    /// </summary>
    [HttpPatch("{table}/{id}")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Patch(string table, string id, [FromBody] JsonElement body)
    {
        var tableDef = ResolveTable(table);
        var row = await FreshAsync(tableDef, await _crudService.PatchAsync(tableDef, id, body));
        return Ok(new { data = row });
    }

    /// <summary>
    /// Delete a row by ID.
    /// </summary>
    [HttpDelete("{table}/{id}")]
    [RequireRole("editor", "api")]
    public async Task<IActionResult> Delete(string table, string id)
    {
        var tableDef = ResolveTable(table);
        await _crudService.DeleteAsync(tableDef, id);
        _recalculation?.Request();
        return NoContent();
    }

    /// <summary>
    /// The written row with its CUSTOM values recalculated (Step 4.1); other
    /// tables' stored values follow in the background.
    /// </summary>
    private async Task<IDictionary<string, object?>> FreshAsync(TableDef table, IDictionary<string, object?> row)
    {
        if (_recalculation is null || !await _recalculation.RecalculateNowAsync(table))
            return row;
        var key = table.Columns.FirstOrDefault(c => c.PrimaryKey);
        if (key is null || !row.TryGetValue(key.Name, out var id) || id is null)
            return row;
        return await _crudService.GetByIdAsync(table, System.Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture)!);
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
