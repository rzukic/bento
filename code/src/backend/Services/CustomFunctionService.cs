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

/// <summary>
/// The saved custom functions in memory, for the formula compiler (reloaded on
/// every change) — one set per workspace, read for the current one.
/// </summary>
public sealed class CustomFunctionCatalog : ICustomFunctionCatalog
{
    private static readonly IReadOnlyDictionary<string, CustomFunctionDef> None =
        new Dictionary<string, CustomFunctionDef>(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (IReadOnlyDictionary<string, CustomFunctionDef> Functions, long Version)> _byWorkspace = new();
    private long _version;

    // One counter for all workspaces: a version names one set of one workspace.
    public long Version => _byWorkspace.TryGetValue(Workspaces.WorkspaceContext.Id, out var set) ? set.Version : 0;

    public IReadOnlyDictionary<string, CustomFunctionDef> Functions =>
        _byWorkspace.TryGetValue(Workspaces.WorkspaceContext.Id, out var set) ? set.Functions : None;

    /// <summary>Replaces the current workspace's functions.</summary>
    public void Replace(IEnumerable<CustomFunctionDef> functions) =>
        _byWorkspace[Workspaces.WorkspaceContext.Id] =
            (functions.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase), Interlocked.Increment(ref _version));

    /// <summary>The catalog with one function changed (or removed), to check a change before saving it.</summary>
    public ICustomFunctionCatalog With(CustomFunctionDef? changed, string? removed = null)
    {
        var functions = Functions.Values.Where(f =>
            !string.Equals(f.Name, changed?.Name ?? removed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (changed is not null)
            functions.Add(changed);
        return new StaticFunctionCatalog(functions, Interlocked.Read(ref _version) + 1);
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
    private readonly CredentialService? _credentials;
    private readonly JsNetwork? _network;
    private readonly ILogger<CustomFunctionService>? _logger;

    public CustomFunctionService(
        ISystemPool systemPool,
        CustomFunctionCatalog catalog,
        SchemaCache schemaCache,
        DatabaseConfig dbConfig,
        ICustomValueRecalculation? recalculation = null,
        CredentialService? credentials = null,
        JsNetwork? network = null,
        ILogger<CustomFunctionService>? logger = null)
    {
        _systemPool = systemPool;
        _catalog = catalog;
        _schemaCache = schemaCache;
        _dbConfig = dbConfig;
        _recalculation = recalculation;
        _credentials = credentials;
        _network = network;
        _logger = logger;
    }

    private static long Ws => Workspaces.WorkspaceContext.Id;
    private bool Pg => _systemPool.Provider == DatabaseProvider.Postgres;
    private string Table => Pg ? "_system.functions" : "functions";
    private string Versions => Pg ? "_system.function_versions" : "function_versions";
    private string Dashboards => Pg ? "_system.dashboards" : "dashboards";
    private string PriceSources => Pg ? "_system.price_sources" : "price_sources";

    /// <summary>Loads every function into the catalog (start-up and after each change).</summary>
    public async Task ReloadAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = await conn.QueryAsync<FunctionRow>($"SELECT {Columns} FROM {Table} WHERE workspace_id = @Ws", new { Ws });
        _catalog.Replace(rows.Select(ToDefinition));
        _logger?.LogInformation("Custom functions loaded: {Count}.", _catalog.Functions.Count);
    }

    public async Task<IReadOnlyList<CustomFunctionResponse>> ListAsync()
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var rows = (await conn.QueryAsync<FunctionRow>($"SELECT {Columns} FROM {Table} WHERE workspace_id = @Ws ORDER BY name", new { Ws })).ToList();
        var others = await OtherUsagesAsync(conn);
        return rows.Select(r => ToResponse(r, others)).ToList();
    }

    public async Task<CustomFunctionResponse> GetAsync(string name)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        return ToResponse(await RequireAsync(conn, name), await OtherUsagesAsync(conn));
    }

    public async Task<IReadOnlyList<CustomFunctionVersionResponse>> VersionsAsync(string name)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        var current = await RequireAsync(conn, name);
        var rows = await conn.QueryAsync<FunctionRow>(
            $"""
            SELECT function_name AS Name, NULL AS Description, CAST(arguments AS TEXT) AS Arguments, result_type AS ResultType, code, version,
                   CAST(allowed_hosts AS TEXT) AS AllowedHosts, CAST(credentials AS TEXT) AS Credentials, mode,
                   {SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt,
                   {SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS UpdatedAt
            FROM {Versions} WHERE workspace_id = @Ws AND function_name = @Name ORDER BY version DESC
            """, new { current.Name, Ws });
        return rows.Select(r => new CustomFunctionVersionResponse(r.Version, ParseArguments(r.Arguments), r.ResultType, r.Code, r.CreatedAt,
            ParseList(r.AllowedHosts), ParseList(r.Credentials))).ToList();
    }

    public async Task<CustomFunctionResponse> CreateAsync(CustomFunctionRequest request)
    {
        var name = request.Name?.Trim().ToLowerInvariant() ?? "";
        if (!CustomFunctionRules.IsValidName(name))
            throw Invalid("name", "A function name uses small letters, digits and _ and starts with a letter, e.g. risk_class.");
        if (FormulaFunctions.Find(name) is not null)
            throw Invalid("name", $"{name.ToUpperInvariant()} is a built-in formula function; choose another name.");
        var definition = Validate(request, name, 1);
        await RequireCredentialsAsync(definition);

        using var conn = await _systemPool.GetConnectionAsync();
        if (await FindAsync(conn, name) is not null)
            throw new ApiConflictException($"A function named '{name}' already exists.", new Dictionary<string, object?> { ["field"] = "name" });

        var now = UtcNowIso();
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Table} (workspace_id, name, description, arguments, result_type, code, allowed_hosts, credentials, mode, version, created_at, updated_at)
            VALUES (@Ws, @Name, @Description, {JsonParam("@Arguments")}, @ResultType, @Code, {JsonParam("@AllowedHosts")}, {JsonParam("@Credentials")}, @Mode, 1, {NowParam}, {NowParam})
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
            Code = request.Code ?? existing.Code,
            AllowedHosts = request.AllowedHosts ?? ParseList(existing.AllowedHosts).ToList(),
            Credentials = request.Credentials ?? ParseList(existing.Credentials).ToList(),
            Mode = request.Mode ?? existing.Mode
        };
        var previous = ToDefinition(existing);
        var changed = Validate(merged, existing.Name, existing.Version);
        await RequireCredentialsAsync(changed);
        var codeChanged = changed.Code != previous.Code || changed.ResultType != previous.ResultType ||
                          !changed.Arguments.SequenceEqual(previous.Arguments) ||
                          !changed.AllowedHosts.SequenceEqual(previous.AllowedHosts) || !changed.Credentials.SequenceEqual(previous.Credentials) ||
                          changed.Mode != previous.Mode;
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
                allowed_hosts = {JsonParam("@AllowedHosts")}, credentials = {JsonParam("@Credentials")}, mode = @Mode,
                version = @Version, updated_at = {NowParam}
            WHERE workspace_id = @Ws AND name = @Name
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
        var sources = (await OtherUsagesAsync(conn)).Where(o => o.Function == existing.Name.ToLowerInvariant() && o.Usage.Kind == "priceSource").ToList();
        if (sources.Count > 0)
            throw new ApiConflictException(
                $"'{existing.Name}' runs in the price source {string.Join(", ", sources.Select(s => s.Usage.Name))}; change or delete that source first.",
                new Dictionary<string, object?> { ["priceSources"] = sources.Select(s => s.Usage.Name).ToList() });

        await conn.ExecuteAsync($"DELETE FROM {Table} WHERE workspace_id = @Ws AND name = @Name", new { existing.Name, Ws });
        await conn.ExecuteAsync($"DELETE FROM {Versions} WHERE workspace_id = @Ws AND function_name = @Name", new { existing.Name, Ws });
        await ReloadAsync();
    }

    /// <summary>Runs code with test values; nothing is saved. Functions with hosts really call them.</summary>
    public async Task<CustomFunctionTestResponse> TestAsync(CustomFunctionTestRequest request)
    {
        var definition = Validate(new CustomFunctionRequest
        {
            Arguments = request.Arguments,
            ResultType = request.ResultType,
            Code = request.Code,
            AllowedHosts = request.AllowedHosts,
            Credentials = request.Credentials,
            Mode = request.Mode
        }, "test", 0, checkSyntax: false);

        var values = (request.Values ?? []).Select(FromJson).ToList();
        var (function, error) = JsFunction.Create(definition, host: await HostAsync(definition));
        if (function is null)
            return new CustomFunctionTestResponse(null, definition.ResultType, error, [], 0);
        using (function)
        {
            if (definition.RowsMode)
            {
                // Each argument is a list (JSON array) with a value per row.
                var columns = (request.Values ?? []).Select(v => v.ValueKind == JsonValueKind.Array
                    ? (IReadOnlyList<object?>)v.EnumerateArray().Select(FromJson).ToList()
                    : [FromJson(v)]).ToList();
                var count = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var rows = await Task.Run(() => function.CallRows(columns, count));
                var ms = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2);
                return new CustomFunctionTestResponse(rows.Values?.Select(v => Shape(v, definition)).ToList(), definition.ResultType, rows.Error, rows.Logs, ms);
            }
            // fetch blocks while it waits for answers: run off the request thread.
            var result = await Task.Run(() => function.Call(values));
            return new CustomFunctionTestResponse(
                result.Ok ? Shape(result.Value, definition) : null,
                definition.ResultType, result.Error, result.Logs, result.DurationMs, function.Requests);
        }
    }

    /// <summary>What a function may use when it runs: the network and the credentials it names.</summary>
    public async Task<JsHost> HostAsync(CustomFunctionDef definition)
    {
        if (!definition.UsesNetwork)
            return JsHost.None;
        var secrets = _credentials is null || definition.Credentials.Count == 0
            ? new Dictionary<string, string>()
            : await _credentials.ResolveAsync(definition.Credentials);
        return new JsHost(_network, secrets);
    }

    /// <summary>A result in the API's shape; "any" stays JSON.</summary>
    public static object? Shape(object? value, CustomFunctionDef definition) =>
        definition.ResultType == "any" ? value : ResultValues.Shape(value, definition.Result);

    private async Task RequireCredentialsAsync(CustomFunctionDef definition)
    {
        if (_credentials is null || definition.Credentials.Count == 0)
            return;
        var missing = await _credentials.MissingAsync(definition.Credentials);
        if (missing.Count > 0)
            throw Invalid("credentials", $"No credential named {string.Join(", ", missing.Select(m => $"'{m}'"))}; add it under Credentials first.");
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
                   (string.Equals(name, "CUSTOM", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "CUSTOM_ROWS", StringComparison.OrdinalIgnoreCase))
                ? literal.Value.Trim()
                : null;
        }
        catch (FormulaSyntaxException)
        {
            return null;
        }
    }

    private static readonly Regex DashboardCall = new("CUSTOM\\(\\s*\\\\\"([a-z][a-z0-9_]*)\\\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Dashboards whose formulas call a function, and price sources that run one (Step 4.2).</summary>
    private async Task<List<(string Function, CustomFunctionUsage Usage)>> OtherUsagesAsync(System.Data.IDbConnection conn)
    {
        var usages = new List<(string, CustomFunctionUsage)>();
        foreach (var row in await conn.QueryAsync<DashboardRow>($"SELECT id, name, CAST(dashboard_json AS TEXT) AS Json FROM {Dashboards} WHERE workspace_id = @Ws", new { Ws }))
        {
            foreach (var function in DashboardCall.Matches(row.Json ?? "").Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct())
                usages.Add((function, new CustomFunctionUsage("dashboard", row.Name, DashboardId: row.Id)));
        }
        foreach (var row in await conn.QueryAsync<DashboardRow>($"SELECT id, name, CAST(config AS TEXT) AS Json FROM {PriceSources} WHERE workspace_id = @Ws", new { Ws }))
        {
            var config = JsonSerializer.Deserialize<PriceSourceConfig>(row.Json ?? "{}", Json);
            foreach (var function in new[] { config?.Function, config?.LookupFunction }.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
                usages.Add((function!.ToLowerInvariant(), new CustomFunctionUsage("priceSource", row.Name, SourceId: row.Id)));
        }
        return usages;
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

        var hosts = new List<string>();
        foreach (var (host, index) in (request.AllowedHosts ?? []).Select((h, i) => (h?.Trim().ToLowerInvariant() ?? "", i)))
        {
            if (!CustomFunctionRules.IsValidHost(host))
                throw Invalid($"allowedHosts[{index}]", $"'{host}' is not a host name; write it without https:// or a path, e.g. query1.finance.yahoo.com or *.example.com.");
            if (!hosts.Contains(host))
                hosts.Add(host);
        }
        if (hosts.Count > CustomFunctionRules.MaxHosts)
            throw Invalid("allowedHosts", $"A function may call at most {CustomFunctionRules.MaxHosts} hosts.");

        var credentials = new List<string>();
        foreach (var (credential, index) in (request.Credentials ?? []).Select((c, i) => (c?.Trim().ToLowerInvariant() ?? "", i)))
        {
            if (!CustomFunctionRules.IsValidName(credential))
                throw Invalid($"credentials[{index}]", $"'{credential}' is not a credential name.");
            if (!credentials.Contains(credential))
                credentials.Add(credential);
        }
        if (credentials.Count > 0 && hosts.Count == 0)
            throw Invalid("credentials", "Credentials are for calling APIs; add the hosts the function calls.");

        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "row" : request.Mode.Trim().ToLowerInvariant();
        if (mode is not ("row" or "rows"))
            throw Invalid("mode", "mode is \"row\" (one call per row) or \"rows\" (one call per group of rows, for CUSTOM_ROWS).");
        if (mode == "rows" && hosts.Count > 0)
            throw Invalid("mode", "A function over rows computes from the rows; it cannot call the internet.");
        if (mode == "rows" && resultType == "any")
            throw Invalid("resultType", "A function over rows gives a value per row of a declared type.");

        var definition = new CustomFunctionDef(name, description, arguments, resultType, code, version)
        {
            AllowedHosts = hosts,
            Credentials = credentials,
            Mode = mode
        };
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
        JsonValueKind.Array or JsonValueKind.Object => value.Clone(),
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
        public string AllowedHosts { get; init; } = "[]";
        public string Credentials { get; init; } = "[]";
        public string Mode { get; init; } = "row";
        public int Version { get; init; }
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
    }

    private string Columns =>
        "name, description, CAST(arguments AS TEXT) AS Arguments, result_type AS ResultType, code, version, " +
        "CAST(allowed_hosts AS TEXT) AS AllowedHosts, CAST(credentials AS TEXT) AS Credentials, mode, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "created_at")} AS CreatedAt, " +
        $"{SystemSqlDialect.TimestampText(_systemPool.Provider, "updated_at")} AS UpdatedAt";

    private async Task<FunctionRow?> FindAsync(System.Data.IDbConnection conn, string name) =>
        await conn.QuerySingleOrDefaultAsync<FunctionRow>(
            $"SELECT {Columns} FROM {Table} WHERE workspace_id = @Ws AND LOWER(name) = LOWER(@Name)", new { Name = name.Trim(), Ws });

    private async Task<FunctionRow> RequireAsync(System.Data.IDbConnection conn, string name) =>
        await FindAsync(conn, name) ?? throw new ApiNotFoundException($"Function '{name}' not found.");

    private async Task SaveVersionAsync(System.Data.IDbConnection conn, CustomFunctionDef definition, string now) =>
        await conn.ExecuteAsync(
            $"""
            INSERT INTO {Versions} (workspace_id, function_name, version, arguments, result_type, code, allowed_hosts, credentials, mode, created_at)
            VALUES (@Ws, @Name, @Version, {JsonParam("@Arguments")}, @ResultType, @Code, {JsonParam("@AllowedHosts")}, {JsonParam("@Credentials")}, @Mode, {NowParam})
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
        AllowedHosts = JsonSerializer.Serialize(definition.AllowedHosts, Json),
        Credentials = JsonSerializer.Serialize(definition.Credentials, Json),
        definition.Mode,
        Ws,
        Now = now
    };

    private static CustomFunctionDef ToDefinition(FunctionRow row) =>
        new(row.Name, row.Description, ParseArguments(row.Arguments), row.ResultType, row.Code, row.Version)
        {
            AllowedHosts = ParseList(row.AllowedHosts),
            Credentials = ParseList(row.Credentials),
            Mode = string.IsNullOrEmpty(row.Mode) ? "row" : row.Mode
        };

    private static IReadOnlyList<string> ParseList(string? json) =>
        JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, Json) ?? [];

    private static IReadOnlyList<CustomFunctionArgument> ParseArguments(string json) =>
        JsonSerializer.Deserialize<List<CustomFunctionArgument>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, Json) ?? [];

    private CustomFunctionResponse ToResponse(FunctionRow row, List<(string Function, CustomFunctionUsage Usage)> others)
    {
        var usedBy = ComputedUsages(_catalog)
            .Where(u => string.Equals(u.Function, row.Name, StringComparison.OrdinalIgnoreCase))
            .Select(u => new CustomFunctionUsage("column", $"{u.Table}.{u.Column}", u.Table, u.Column))
            .Concat(others.Where(o => o.Function == row.Name.ToLowerInvariant()).Select(o => o.Usage))
            .ToList();
        var definition = ToDefinition(row);
        return new CustomFunctionResponse(row.Name, row.Description, definition.Arguments, row.ResultType, row.Code,
            row.Version, row.CreatedAt, row.UpdatedAt, usedBy, definition.AllowedHosts, definition.Credentials, definition.UsableInFormulas, definition.Mode);
    }

    private string JsonParam(string parameter) => SystemSqlDialect.JsonParam(_systemPool.Provider, parameter);
    private string NowParam => SystemSqlDialect.TimestampParam(_systemPool.Provider, "@Now");

    private static string UtcNowIso() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });
}

/// <summary>Loads the custom functions before the schema is checked and migrated on start-up.</summary>
public sealed class CustomFunctionCatalogInitializer(CustomFunctionService functions, Workspaces.WorkspaceService? workspaces = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var workspace in workspaces is null ? [Workspaces.Workspace.Default] : await workspaces.ReloadAsync())
        {
            using var _ = Workspaces.WorkspaceContext.Use(workspace);
            await functions.ReloadAsync();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
