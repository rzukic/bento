using System.Data.Common;
using Dapper;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Formula;
using Bento.Api.Models;

namespace Bento.Api.Services;

/// <summary>
/// Formulas against the live schema (Step 3.4): validation as the user types
/// and a preview of the values on real rows, computed by the database on
/// either provider.
/// </summary>
public sealed class FormulaService
{
    public const int DefaultPreviewLimit = 20;
    public const int MaxPreviewLimit = 100;

    private readonly SchemaCache _schemaCache;
    private readonly QueryService _queryService;
    private readonly DatabaseConfig _dbConfig;

    public FormulaService(SchemaCache schemaCache, QueryService queryService, DatabaseConfig dbConfig)
    {
        _schemaCache = schemaCache;
        _queryService = queryService;
        _dbConfig = dbConfig;
    }

    public FormulaValidateResponse Validate(FormulaRequest request)
    {
        var (_, check) = Check(request, draft: request.Columns is { ValueKind: not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) });
        return new FormulaValidateResponse(
            check.IsValid,
            check.Result is { } result ? FormulaTypes.Name(result.Type) : null,
            check.Errors.Select(e => new FormulaErrorResponse(e.Message, e.Start, e.End)).ToList(),
            check.Result?.Columns ?? []);
    }

    public async Task<FormulaPreviewResponse> PreviewAsync(FormulaRequest request)
    {
        var limit = request.Limit ?? DefaultPreviewLimit;
        if (limit < 1 || limit > MaxPreviewLimit)
            throw Invalid("limit", $"limit must be between 1 and {MaxPreviewLimit}.");

        var (table, check) = Check(request, draft: false);
        if (!check.IsValid)
            throw new ApiValidationException(check.Errors[0].Message, new Dictionary<string, object?>
            {
                ["field"] = "formula",
                ["errors"] = check.Errors.Select(e => new FormulaErrorResponse(e.Message, e.Start, e.End)).ToList()
            });

        var compiled = check.Result!;
        var key = table.Columns.FirstOrDefault(c => c.PrimaryKey);
        var inputs = compiled.Columns.Select(name => table.Columns.First(c => c.Name == name)).ToList();

        var select = new List<string>();
        if (key is not null)
            select.Add($"t.{Q(key.Name)} AS \"__id\"");
        select.AddRange(inputs.Select((c, i) => $"t.{Q(c.Name)} AS \"__c{i}\""));
        select.Add($"{compiled.Sql} AS \"__value\"");
        var sql = $"SELECT {string.Join(", ", select)} FROM {Q(table.Name)} t" +
            (key is null ? "" : $" ORDER BY t.{Q(key.Name)}") + $" LIMIT {limit}";

        await using var scope = await _queryService.UseDataAsync();
        List<IDictionary<string, object?>> rows;
        try
        {
            rows = (await scope.Db.Connection.QueryAsync(sql)).Cast<IDictionary<string, object?>>().ToList();
        }
        catch (DbException e)
        {
            throw new ApiUnprocessableEntityException($"The formula could not be computed on the data: {e.Message}");
        }

        var preview = rows.Select(row =>
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < inputs.Count; i++)
            {
                var raw = row[$"__c{i}"];
                if (inputs[i].Type == ColumnType.DateTime && raw is string text && !text.Contains('T'))
                    raw = text.Replace(' ', 'T') + "Z";
                values[inputs[i].Name] = ResultValues.Shape(raw, inputs[i].Type);
            }
            var id = key is null ? null : ResultValues.Shape(row["__id"], key.Type);
            return new FormulaPreviewRow(id, ResultValues.Shape(row["__value"], compiled.Type), values);
        }).ToList();

        return new FormulaPreviewResponse(FormulaTypes.Name(compiled.Type), compiled.Columns, preview);
    }

    private (TableDef Table, FormulaCheck Check) Check(FormulaRequest request, bool draft)
    {
        if (string.IsNullOrWhiteSpace(request.Table))
            throw Invalid("table", "Choose the table the formula belongs to.");
        var applied = _schemaCache.GetTable(request.Table.Trim());
        var table = draft
            ? new TableDef { Name = request.Table.Trim(), DisplayName = applied?.DisplayName, Columns = DraftColumns(request.Columns!.Value) }
            : applied ?? throw new ApiNotFoundException($"Table '{request.Table}' does not exist.",
                new Dictionary<string, object?> { ["field"] = "table", ["table"] = request.Table });
        if (request.Formula is null)
            throw Invalid("formula", "formula is required.");
        if (request.Formula.Length > 4000)
            throw Invalid("formula", "The formula is too long (at most 4000 characters).");

        FormulaType? expected = null;
        if (!string.IsNullOrWhiteSpace(request.ResultType))
        {
            expected = FormulaTypes.FromName(request.ResultType)
                ?? throw Invalid("resultType", $"Unknown result type '{request.ResultType}'; use text, integer, float, currency, boolean, date or datetime.");
        }

        var check = FormulaCompiler.Compile(request.Formula, table, _schemaCache.GetTable, _dbConfig.Provider, expected: expected);
        return (table, check);
    }

    private static List<ColumnDef> DraftColumns(System.Text.Json.JsonElement columns)
    {
        if (columns.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw Invalid("columns", "columns must be an array of column definitions.");
        try
        {
            return ModelJsonSerializer.ParseColumns(columns);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw Invalid("columns", $"columns are not valid column definitions: {e.Message}");
        }
    }

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}
