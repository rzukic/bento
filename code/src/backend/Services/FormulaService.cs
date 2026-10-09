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
    private readonly ComputedColumns _computed;

    public FormulaService(SchemaCache schemaCache, QueryService queryService, DatabaseConfig dbConfig, ComputedColumns computed)
    {
        _schemaCache = schemaCache;
        _queryService = queryService;
        _dbConfig = dbConfig;
        _computed = computed;
    }

    private ICustomFunctionCatalog Functions => _computed.Functions;

    public FormulaValidateResponse Validate(FormulaRequest request)
    {
        if (string.Equals(request.Mode?.Trim(), "measure", StringComparison.OrdinalIgnoreCase))
            return ValidateMeasure(request);
        if (!string.IsNullOrWhiteSpace(request.Mode) && !string.Equals(request.Mode.Trim(), "row", StringComparison.OrdinalIgnoreCase))
            throw Invalid("mode", "mode is \"row\" (a column formula) or \"measure\" (a dashboard measure).");

        var (_, check) = Check(request, draft: Present(request.Columns) || Present(request.Model));
        return new FormulaValidateResponse(
            check.IsValid,
            check.Result is { } result ? FormulaTypes.Name(result.Type) : null,
            check.Errors.Select(e => new FormulaErrorResponse(e.Message, e.Start, e.End)).ToList(),
            check.Result?.Columns ?? []);
    }

    /// <summary>A dashboard measure over the applied table's rows.</summary>
    private FormulaValidateResponse ValidateMeasure(FormulaRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Table))
            throw Invalid("table", "Choose the table the measure runs over.");
        var table = _schemaCache.GetTable(request.Table.Trim())
            ?? throw new ApiNotFoundException($"Table '{request.Table}' does not exist.",
                new Dictionary<string, object?> { ["field"] = "table", ["table"] = request.Table });
        var (check, running) = FormulaCompiler.CompileMeasure(request.Formula ?? "", table, FormulaSchema.From(_schemaCache.Current), _dbConfig.Provider,
            context: new FormulaContext
            {
                BucketAlias = request.AsOf ? "b" : null,
                Variables = AggregationQueryService.ResolveVariables(request.Variables),
                Functions = Functions
            });
        return new FormulaValidateResponse(
            check.IsValid,
            check.Result is { } result ? FormulaTypes.Name(result.Type) : null,
            check.Errors.Select(e => new FormulaErrorResponse(e.Message, e.Start, e.End)).ToList(),
            check.Result?.Columns ?? [],
            running);
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

        // The row is addressed by the quoted table name, like in list queries.
        var row = Q(table.Name);
        var computed = _computed.For(table);
        var select = new List<string>();
        if (key is not null)
            select.Add($"{row}.{Q(key.Name)} AS \"__id\"");
        select.AddRange(inputs.Select((c, i) => computed.TryGetValue(c.Name, out var input)
            ? $"{input.Sql} AS \"__c{i}\""
            : $"{row}.{Q(c.Name)} AS \"__c{i}\""));
        // CUSTOM(…) runs here on each row, so a draft column previews before anything is stored.
        var custom = compiled.Custom;
        if (custom is null || custom.OverRows)
            select.Add($"{(custom is null ? compiled.Sql : "NULL")} AS \"__value\"");
        else
            select.AddRange(custom.Arguments.Select((a, i) => $"{a.Sql} AS \"__a{i}\""));
        var sql = $"SELECT {string.Join(", ", select)} FROM {row}" +
            (key is null ? "" : $" ORDER BY {row}.{Q(key.Name)}") + $" LIMIT {limit}";

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

        if (custom is { OverRows: true } && key is not null)
        {
            // CUSTOM_ROWS: a row's value depends on its whole group, so the groups of all rows run.
            List<IDictionary<string, object?>> all;
            try
            {
                all = (await scope.Db.Connection.QueryAsync(CustomRows.SelectSql(custom, row, Q(key.Name))))
                    .Cast<IDictionary<string, object?>>().ToList();
            }
            catch (DbException e)
            {
                throw new ApiUnprocessableEntityException($"The formula could not be computed on the data: {e.Message}");
            }
            var shown = rows.Select(r => System.Convert.ToString(r["__id"], System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
            var (function, error) = Formula.Sandbox.JsFunction.Create(custom.Function);
            var results = new Dictionary<string, CustomRows.Result>();
            using (function)
            {
                foreach (var group in CustomRows.Groups(custom, all).Where(g => g.Keys.Any(shown.Contains)))
                    foreach (var result in CustomRows.Run(function, error, custom, group))
                        results[result.Key] = result;
            }
            foreach (var values in rows)
            {
                if (!results.TryGetValue(System.Convert.ToString(values["__id"], System.Globalization.CultureInfo.InvariantCulture)!, out var result))
                    continue;
                values["__value"] = result.Value;
                values["__error"] = result.Error;
            }
        }
        else if (custom is not null)
        {
            var (function, error) = Formula.Sandbox.JsFunction.Create(custom.Function);
            using (function)
            {
                foreach (var values in rows)
                {
                    var arguments = custom.Arguments.Select((a, i) => ResultValues.Shape(values[$"__a{i}"], a.Type)).ToList();
                    var result = function?.Call(arguments);
                    values["__value"] = result is { Ok: true } ? result.Value : null;
                    values["__error"] = result?.Error ?? error;
                }
            }
        }

        var preview = rows.Select(row =>
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < inputs.Count; i++)
            {
                var raw = row[$"__c{i}"];
                if (computed.TryGetValue(inputs[i].Name, out var input))
                {
                    values[inputs[i].Name] = ResultValues.Shape(raw, input.Type);
                    continue;
                }
                if (inputs[i].Type == ColumnType.DateTime && raw is string text && !text.Contains('T'))
                    raw = text.Replace(' ', 'T') + "Z";
                values[inputs[i].Name] = ResultValues.Shape(raw, inputs[i].Type);
            }
            var id = key is null ? null : ResultValues.Shape(row["__id"], key.Type);
            return new FormulaPreviewRow(id, ResultValues.Shape(row["__value"], compiled.Type), values,
                row.TryGetValue("__error", out var problem) ? problem as string : null);
        }).ToList();

        return new FormulaPreviewResponse(FormulaTypes.Name(compiled.Type), compiled.Columns, preview);
    }

    private (TableDef Table, FormulaCheck Check) Check(FormulaRequest request, bool draft)
    {
        if (string.IsNullOrWhiteSpace(request.Table))
            throw Invalid("table", "Choose the table the formula belongs to.");
        var name = request.Table.Trim();
        var applied = _schemaCache.GetTable(name);
        ModelJson? draftModel = draft && Present(request.Model) ? DraftModel(request.Model!.Value) : null;
        var table = draftModel is not null
            ? draftModel.Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid("table", $"The draft has no table '{name}'.")
            : draft
                ? new TableDef { Name = name, DisplayName = applied?.DisplayName, Columns = DraftColumns(request.Columns!.Value) }
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

        var schema = draftModel is not null
            ? FormulaSchema.From(draftModel)
            : draft ? FormulaSchema.WithTable(_schemaCache.Current, table) : FormulaSchema.From(_schemaCache.Current);
        var check = FormulaCompiler.Compile(request.Formula, table, schema, _dbConfig.Provider,
            alias: Q(table.Name), expected: expected, column: string.IsNullOrWhiteSpace(request.Column) ? null : request.Column.Trim(),
            context: new FormulaContext { Functions = Functions });
        return (table, check);
    }

    private static bool Present(System.Text.Json.JsonElement? value) =>
        value is { ValueKind: not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) };

    private static ModelJson DraftModel(System.Text.Json.JsonElement model)
    {
        if (model.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw Invalid("model", "model must be a Model JSON object.");
        try
        {
            return ModelJsonSerializer.Parse(model.GetRawText());
        }
        catch (System.Text.Json.JsonException e)
        {
            throw Invalid("model", $"model is not valid Model JSON: {e.Message}");
        }
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
