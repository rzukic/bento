using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Exceptions;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;
using Bento.Api.Models;
using Dapper;

namespace Bento.Api.Services;

/// <summary>The saved custom functions in memory, for the formula compiler (reloaded on every change).</summary>
public sealed class CustomFunctionCatalog : ICustomFunctionCatalog
{
    private volatile IReadOnlyDictionary<string, CustomFunctionDef> _functions =
        new Dictionary<string, CustomFunctionDef>(StringComparer.OrdinalIgnoreCase);
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public IReadOnlyDictionary<string, CustomFunctionDef> Functions => _functions;

    public void Replace(IEnumerable<CustomFunctionDef> functions)
    {
        _functions = functions.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        Interlocked.Increment(ref _version);
    }

    /// <summary>The catalog with one function changed (or removed), to check a change before saving it.</summary>
    public ICustomFunctionCatalog With(CustomFunctionDef? changed, string? removed = null)
    {
        var functions = _functions.Values.Where(f =>
            !string.Equals(f.Name, changed?.Name ?? removed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (changed is not null)
            functions.Add(changed);
        return new StaticFunctionCatalog(functions, Version + 1);
    }
}

/// <summary>Asks the background recalculation of stored CUSTOM values to run (Step 4.1, Tier 3).</summary>
public interface ICustomValueRecalculation
{
    void Request();

    /// <summary>
    /// Recalculates at once when the table has CUSTOM columns, so a single-row
    /// write answers with fresh values; false when it has none (or it took too long).
    /// </summary>
    Task<bool> RecalculateNowAsync(Db.Schema.TableDef table);
}

/// <summary>
/// Custom functions (Step 4.1, Part 5 R5.5): stored in <c>_system.functions</c>
/// with every saved version in <c>_system.function_versions</c>, checked in
/// the sandbox, and listed with the computed columns and dashboards that use
/// them. A change that would break a computed column is refused.
/// </summary>
public sealed class CustomFunctionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ISystemPool _systemPool;
    private readonly CustomFunctionCatalog _catalog;
    private readonly SchemaCache _schemaCache;
    private readonly DatabaseConfig _dbConfig;
    private readonly ICustomValueRecalculation? _recalculation;
    private readonly ILogger<CustomFunctionService>? _logger;

    public CustomFunctionService(
        ISystemPool systemPool,
        CustomFunctionCatalog catalog,
        SchemaCache schemaCache,
        DatabaseConfig dbConfig,
        ICustomValueRecalculation? recalculation = null,
        ILogger<CustomFunctionService>? logger = null)
    {
        _systemPool = systemPool;
        _catalog = catalog;
        _schemaCache = schemaCache;
        _dbConfig = dbConfig;
        _recalculation = recalculation;
        _logger = logger;
    }

    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Table => Pg ? "_system.functions" : "functions";
    private string Versions => Pg ? "_system.function_versions" : "function_versions";
    private string Dashboards => Pg ? "_system.dashboards" : "dashboards";

    /// <summary>Loads every function into the catalog (start-up and after each change).</summary>
    public async Task ReloadAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<FunctionRow>($"SELECT {Columns} FROM {Table}");
        _catalog.Replace(rows.Select(ToDefinition));
        _logger?.LogInformation("Custom functions loaded: {Count}.", _catalog.Functions.Count);
    }

    public async Task<IReadOnlyList<CustomFunctionResponse>> ListAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = (await conn.QueryAsync<FunctionRow>($"SELECT {Columns} FROM {Table} ORDER BY name")).ToList();
        var dashboards = await DashboardUsagesAsync(conn);
        return rows.Select(r => ToResponse(r, dashboards)).ToList();
    }

    public async Task<CustomFunctionResponse> GetAsync(string name)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        return ToResponse(await RequireAsync(conn, name), await DashboardUsagesAsync(conn));
    }

    public async Task<IReadOnlyList<CustomFunctionVersionResponse>> VersionsAsync(string name)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var current = await RequireAsync(conn, name);
        var rows = await conn.QueryAsync<FunctionRow>(
            $"""
            SELECT function_name AS Name, NULL AS Description, arguments, result_type AS ResultType, code, version,
                   {SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt,
                   {SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS UpdatedAt
            FROM {Versions} WHERE function_name = @Name ORDER BY version DESC
            """, new { current.Name });
        return rows.Select(r => new CustomFunctionVersionResponse(r.Version, ParseArguments(r.Arguments), r.ResultType, r.Code, r.CreatedAt)).ToList();
    }

    public async Task<CustomFunctionResponse> CreateAsync(CustomFunctionRequest request)
    {
        var name = request.Name?.Trim().ToLowerInvariant() ?? "";
        if (!CustomFunctionRules.IsValidName(name))
            throw Invalid("name", "A function name uses small letters, digits and _ and starts with a letter, e.g. risk_class.");
        if (FormulaFunctions.Find(name) is not null)
            throw Invalid("name", $"{name.ToUpperInvariant()} is a built-in formula function; choose another name.");
        var definition = Validate(request, name, 1);

        using var conn = await _systemPool.GetConnectionAsync();
        if (await FindAsync(conn, name) is not null)
            throw new ApiConflictException($"A function named '{name}' already exists.", new Dictionary<string, object?> { ["field"] = "name" });

        var now = UtcNowIso();
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Table} (name, description, arguments, result_type, code, version, created_at, updated_at)
            VALUES (@Name, @Description, {JsonParam("@Arguments")}, @ResultType, @Code, 1, {NowParam}, {NowParam})
            """,
            Parameters(definition, now));
        await SaveVersionAsync(conn, definition, now);
        await ReloadAsync();
        return await GetAsync(name);
    }

    public async Task<CustomFunctionResponse> UpdateAsync(string name, CustomFunctionRequest request)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await RequireAsync(conn, name);
        if (request.Name is not null && !string.Equals(request.Name.Trim(), existing.Name, StringComparison.OrdinalIgnoreCase))
            throw Invalid("name", "A function keeps its name; formulas call it by that name. Create a new function instead.");

        var merged = new CustomFunctionRequest
        {
            Description = request.Description ?? existing.Description,
            Arguments = request.Arguments ?? ParseArguments(existing.Arguments).ToList(),
            ResultType = request.ResultType ?? existing.ResultType,
            Code = request.Code ?? existing.Code
        };
        var previous = ToDefinition(existing);
        var changed = Validate(merged, existing.Name, existing.Version);
        var codeChanged = changed.Code != previous.Code || changed.ResultType != previous.ResultType ||
                          !changed.Arguments.SequenceEqual(previous.Arguments);
        var definition = changed with { Version = codeChanged ? existing.Version + 1 : existing.Version };

        // Computed columns that call it must still check with the new signature.
        var broken = ComputedUsages(_catalog.With(definition))
            .Where(u => u.Error is not null)
            .Select(u => $"{u.Table}.{u.Column}: {u.Error}")
            .ToList();
        if (broken.Count > 0)
            throw new ApiConflictException(
                $"The change would break {broken.Count} computed column{(broken.Count == 1 ? "" : "s")}: {string.Join("; ", broken)}",
                new Dictionary<string, object?> { ["columns"] = broken });

        var now = UtcNowIso();
        await conn.ExecuteAsync(
            $"""
            UPDATE {Table}
            SET description = @Description, arguments = {JsonParam("@Arguments")}, result_type = @ResultType, code = @Code,
                version = @Version, updated_at = {NowParam}
            WHERE name = @Name
            """,
            Parameters(definition, now));
        if (codeChanged)
            await SaveVersionAsync(conn, definition, now);
        await ReloadAsync();
        if (codeChanged)
            _recalculation?.Request();
        return await GetAsync(existing.Name);
    }

    public async Task DeleteAsync(string name)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var existing = await RequireAsync(conn, name);
        var columns = ComputedUsages(_catalog).Where(u => string.Equals(u.Function, existing.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (columns.Count > 0)
            throw new ApiConflictException(
                $"'{existing.Name}' is used by {string.Join(", ", columns.Select(c => $"{c.Table}.{c.Column}"))}; change those computed columns first.",
                new Dictionary<string, object?> { ["columns"] = columns.Select(c => $"{c.Table}.{c.Column}").ToList() });

        await conn.ExecuteAsync($"DELETE FROM {Table} WHERE name = @Name", new { existing.Name });
        await conn.ExecuteAsync($"DELETE FROM {Versions} WHERE function_name = @Name", new { existing.Name });
        await ReloadAsync();
    }

    /// <summary>Runs code with test values; nothing is saved.</summary>
    public CustomFunctionTestResponse Test(CustomFunctionTestRequest request)
    {
        var definition = Validate(new CustomFunctionRequest
        {
            Arguments = request.Arguments,
            ResultType = request.ResultType,
            Code = request.Code
        }, "test", 0, checkSyntax: false);

        var values = (request.Values ?? []).Select(FromJson).ToList();
        var (function, error) = JsFunction.Create(definition);
        if (function is null)
            return new CustomFunctionTestResponse(null, definition.ResultType, error, [], 0);
        using (function)
        {
            var result = function.Call(values);
            return new CustomFunctionTestResponse(
                result.Ok ? ResultValues.Shape(result.Value, definition.Result) : null,
                definition.ResultType, result.Error, result.Logs, result.DurationMs);
        }
    }

    // ── Usages ───────────────────────────────────────────────────────────

    public sealed record ComputedUsage(string Table, string Column, string Function, string? Error);

    /// <summary>Computed columns whose formula is CUSTOM("…", …), checked against a catalog.</summary>
    public IReadOnlyList<ComputedUsage> ComputedUsages(ICustomFunctionCatalog catalog)
    {
        var model = _schemaCache.Current;
        var schema = FormulaSchema.From(model);
        var usages = new List<ComputedUsage>();
        foreach (var table in model.Tables)
        {
            foreach (var column in table.Columns.Where(c => c.Type == ColumnType.Computed))
            {
                if (CalledFunction(column.Formula) is not { } function)
                    continue;
                var check = FormulaCompiler.Compile(column.Formula!, table, schema, _dbConfig.Provider,
                    alias: ComputedColumns.Quote(table.Name), expected: FormulaTypes.FromName(column.ResultType), column: column.Name,
                    context: new FormulaContext { Functions = catalog });
                usages.Add(new ComputedUsage(table.Name, column.Name, function, check.IsValid ? null : check.Errors[0].Message));
            }
        }
        return usages;
    }

    /// <summary>The function a formula calls with CUSTOM("name", …) as its whole formula, or null.</summary>
    public static string? CalledFunction(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula) || !formula.Contains("custom", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return FormulaParser.Parse(formula) is FunctionCall { Name: var name, Arguments: [StringLiteral literal, ..] } &&
                   string.Equals(name, "CUSTOM", StringComparison.OrdinalIgnoreCase)
                ? literal.Value.Trim()
                : null;
        }
        catch (FormulaSyntaxException)
        {
            return null;
        }
    }

    private static readonly Regex DashboardCall = new("CUSTOM\\(\\s*\\\\\"([a-z][a-z0-9_]*)\\\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<List<(long Id, string Name, HashSet<string> Functions)>> DashboardUsagesAsync(System.Data.IDbConnection conn)
    {
        var rows = await conn.QueryAsync<DashboardRow>(
            $"SELECT id, name, CAST(dashboard_json AS TEXT) AS Json FROM {Dashboards}");
        return rows
            .Select(r => (r.Id, r.Name, DashboardCall.Matches(r.Json ?? "").Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet()))
            .Where(r => r.Item3.Count > 0)
            .ToList();
    }

    private sealed class DashboardRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string? Json { get; init; }
    }

    // ── Validation ───────────────────────────────────────────────────────

    private static CustomFunctionDef Validate(CustomFunctionRequest request, string name, int version, bool checkSyntax = true)
    {
        var arguments = new List<CustomFunctionArgument>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (argument, index) in (request.Arguments ?? []).Select((a, i) => (a, i)))
        {
            var argName = argument.Name?.Trim() ?? "";
            if (!CustomFunctionRules.IsValidArgumentName(argName))
                throw Invalid($"arguments[{index}].name", $"'{argName}' cannot be an argument name; use letters, digits and _, e.g. volatility.");
            if (!names.Add(argName))
                throw Invalid($"arguments[{index}].name", $"The argument '{argName}' appears twice.");
            var type = CustomFunctionRules.NormalizeType(argument.Type, argument: true)
                ?? throw Invalid($"arguments[{index}].type", $"Unknown type '{argument.Type}'; use {string.Join(", ", CustomFunctionRules.ArgumentTypes)}.");
            arguments.Add(new CustomFunctionArgument(argName, type));
        }
        if (arguments.Count > CustomFunctionRules.MaxArguments)
            throw Invalid("arguments", $"A function takes at most {CustomFunctionRules.MaxArguments} arguments.");

        var resultType = CustomFunctionRules.NormalizeType(request.ResultType ?? "float", argument: false)
            ?? throw Invalid("resultType", $"Unknown result type '{request.ResultType}'; use {string.Join(", ", CustomFunctionRules.ResultTypes)}.");

        var code = request.Code ?? "";
        if (string.IsNullOrWhiteSpace(code))
            throw Invalid("code", "Write the function's code, e.g. return volatility > 0.2 ? \"high\" : \"low\";");
        if (code.Length > CustomFunctionRules.MaxCodeLength)
            throw Invalid("code", $"The code is too long (at most {CustomFunctionRules.MaxCodeLength} characters).");

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > 1000 })
            throw Invalid("description", "The description is too long (at most 1000 characters).");

        var definition = new CustomFunctionDef(name, description, arguments, resultType, code, version);
        if (checkSyntax && JsFunction.CheckSyntax(definition) is { } syntax)
            throw Invalid("code", syntax);
        return definition;
    }

    private static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole : value.GetDecimal(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    // ── Rows ─────────────────────────────────────────────────────────────

    private sealed class FunctionRow
    {
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Arguments { get; init; } = "[]";
        public string ResultType { get; init; } = "float";
        public string Code { get; init; } = "";
        public int Version { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private string Columns =>
        "name, description, CAST(arguments AS TEXT) AS Arguments, result_type AS ResultType, code, version, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at")} AS UpdatedAt";

    private async Task<FunctionRow?> FindAsync(System.Data.IDbConnection conn, string name) =>
        await conn.QuerySingleOrDefaultAsync<FunctionRow>(
            $"SELECT {Columns} FROM {Table} WHERE LOWER(name) = LOWER(@Name)", new { Name = name.Trim() });

    private async Task<FunctionRow> RequireAsync(System.Data.IDbConnection conn, string name) =>
        await FindAsync(conn, name) ?? throw new ApiNotFoundException($"Function '{name}' not found.");

    private async Task SaveVersionAsync(System.Data.IDbConnection conn, CustomFunctionDef definition, string now) =>
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Versions} (function_name, version, arguments, result_type, code, created_at)
            VALUES (@Name, @Version, {JsonParam("@Arguments")}, @ResultType, @Code, {NowParam})
            """,
            Parameters(definition, now));

    private static object Parameters(CustomFunctionDef definition, string now) => new
    {
        definition.Name,
        definition.Description,
        Arguments = JsonSerializer.Serialize(definition.Arguments, Json),
        definition.ResultType,
        definition.Code,
        definition.Version,
        Now = now
    };

    private static CustomFunctionDef ToDefinition(FunctionRow row) =>
        new(row.Name, row.Description, ParseArguments(row.Arguments), row.ResultType, row.Code, row.Version);

    private static IReadOnlyList<CustomFunctionArgument> ParseArguments(string json) =>
        JsonSerializer.Deserialize<List<CustomFunctionArgument>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, Json) ?? [];

    private CustomFunctionResponse ToResponse(FunctionRow row, List<(long Id, string Name, HashSet<string> Functions)> dashboards)
    {
        var usedBy = ComputedUsages(_catalog)
            .Where(u => string.Equals(u.Function, row.Name, StringComparison.OrdinalIgnoreCase))
            .Select(u => new CustomFunctionUsage("column", $"{u.Table}.{u.Column}", u.Table, u.Column))
            .Concat(dashboards.Where(d => d.Functions.Contains(row.Name.ToLowerInvariant()))
                .Select(d => new CustomFunctionUsage("dashboard", d.Name, DashboardId: d.Id)))
            .ToList();
        return new CustomFunctionResponse(row.Name, row.Description, ParseArguments(row.Arguments), row.ResultType, row.Code,
            row.Version, row.CreatedAt, row.UpdatedAt, usedBy);
    }

    private string JsonParam(string parameter) => SystemSqlDialect.JsonParam(_systemPool.Provider, parameter);
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}

/// <summary>Loads the custom functions before the schema is checked and migrated on start-up.</summary>
public sealed class CustomFunctionCatalogInitializer(CustomFunctionService functions) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => functions.ReloadAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
