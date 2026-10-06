using System.Globalization;
using Bento.Api.Db;
using Bento.Api.Db.Schema;

namespace Bento.Api.Formula;

/// <summary>A formula translated into a SQL expression over one row of its table.</summary>
/// <param name="Custom">
/// Set when the formula is a <c>CUSTOM(…)</c> call (Step 4.1): its arguments
/// as SQL, for the caller to run the function on. In a computed column the
/// SQL reads the stored value; elsewhere it is <c>NULL</c>.
/// </param>
/// <param name="Series">
/// Set when the formula is a return over a series (Step 4.3: XIRR, IRR, TWR,
/// REBASE): its parts as SQL, for the query to compute after the database.
/// </param>
public sealed record CompiledFormula(string Sql, FormulaType Type, IReadOnlyList<string> Columns, CustomCall? Custom = null, SeriesCall? Series = null);

/// <summary>
/// XIRR/IRR over cash flows, TWR over values and flows at period ends, REBASE
/// of a measure to its first value (Step 4.3). The database gives the parts per
/// result row; <see cref="Returns"/> computes the result.
/// </summary>
/// <param name="Kind">xirr, irr, twr or rebase.</param>
/// <param name="Parts">
/// XIRR/IRR: "flows" (serialized amount|date;… of many rows) or "amount"/"date"
/// pairs (one flow each). TWR: "value" and "flow". REBASE: "value".
/// </param>
/// <param name="Base">TWR: the index to start at (none: the cumulative return); REBASE: the first value's new value.</param>
public sealed record SeriesCall(string Kind, IReadOnlyList<SeriesPart> Parts, decimal? Base);

public sealed record SeriesPart(string Kind, string Sql);

/// <summary>A <c>CUSTOM("name", …)</c> call: the function and its arguments as SQL over the row (or the group).</summary>
public sealed record CustomCall(CustomFunctionDef Function, IReadOnlyList<CustomArgument> Arguments);

public sealed record CustomArgument(string Sql, FormulaType Type);

public sealed record FormulaCheck(CompiledFormula? Result, IReadOnlyList<FormulaError> Errors)
{
    public bool IsValid => Result is not null && Errors.Count == 0;
}

/// <summary>A dashboard variable's value, used as a constant in formulas.</summary>
public sealed record FormulaValue(FormulaType Type, object? Value);

/// <summary>Where a formula runs, beyond a single row of its table.</summary>
public sealed record FormulaContext
{
    /// <summary>
    /// A measure (Step 3.6): the outermost SUM/COUNT/AVG/MIN/MAX run over the
    /// rows of a group (<c>SUM(amount WHERE amount &gt; 0)</c>); aggregates inside
    /// them run over related rows as in row formulas.
    /// </summary>
    public bool Measure { get; init; }

    /// <summary>The alias of the period in queries "as of" each period end: BUCKET_START()/BUCKET_END().</summary>
    public string? BucketAlias { get; init; }

    /// <summary>Dashboard variables by name, used where no column has that name.</summary>
    public IReadOnlyDictionary<string, FormulaValue>? Variables { get; init; }

    /// <summary>The custom functions <c>CUSTOM("name", …)</c> may call (Step 4.1).</summary>
    public ICustomFunctionCatalog? Functions { get; init; }
}

/// <summary>A compiled measure: <c>RUNNING_SUM(…)</c> around the whole formula makes it a running total.</summary>
public sealed record MeasureCheck(FormulaCheck Check, bool Running);

/// <summary>
/// Type-checks a parsed formula against a table and compiles it into a SQL
/// expression for SQLite or Postgres. Both providers must give the same
/// value for every formula; where their built-ins differ, the SQL is
/// written so they agree:
/// <list type="bullet">
/// <item>Number comparisons, FLOOR/CEIL/ROUND and number-to-text round to 10
///   places first, so float noise on SQLite (REAL) matches Postgres (NUMERIC).</item>
/// <item>Division is never integer division; dividing by zero gives empty.</item>
/// <item>Text comparisons ignore case; ordering compares code points
///   (<c>COLLATE "C"</c> on Postgres, SQLite's binary order).</item>
/// <item>Dates are <c>yyyy-MM-dd</c> text on SQLite (<c>date()</c>) and <c>date</c> on
///   Postgres; date-times are UTC wall time: SQLite <c>datetime()</c>, Postgres
///   <c>timestamptz AT TIME ZONE 'UTC'</c>.</item>
/// <item>SQLite gets Unicode-aware <c>bento_lower/upper</c> and <c>bento_num_text</c>
///   (see <see cref="SqliteFormulaFunctions"/>).</item>
/// </list>
/// Literals are written into the SQL (strings with quotes doubled), so the
/// expression can be embedded in other queries without parameters.
/// </summary>
public sealed class FormulaCompiler
{
    private sealed record Expr(string Sql, FormulaType Type, FormulaNode Node, object? Constant = null, bool IsRows = false)
    {
        public bool IsConstant => Node is NumberLiteral or StringLiteral or BooleanLiteral or NullLiteral;
    }

    /// <summary>The table a column name is looked up in, and the SQL alias of its row.</summary>
    private sealed record Scope(TableDef Table, string Alias);

    /// <summary>State shared by a formula and the computed columns it pulls in.</summary>
    private sealed class Shared(FormulaSchema schema, bool pg, FormulaContext context)
    {
        public FormulaSchema Schema { get; } = schema;
        public bool Pg { get; } = pg;
        public FormulaContext Context { get; } = context;
        /// <summary>"table.column" of the computed columns being compiled, for cycle errors.</summary>
        public List<string> Chain { get; } = [];
        private int _aliases;
        public string NextAlias(string prefix) => $"__{prefix}{++_aliases}";
    }

    /// <summary>The related rows one SUM/COUNT/AVG/MIN/MAX runs over.</summary>
    private sealed class AggregateFrame(Scope outer)
    {
        public Scope Outer { get; } = outer;
        public FormulaNavigation? Navigation { get; set; }
        public Scope? Rows { get; set; }
    }

    private readonly Shared _shared;
    private readonly Scope _root;
    private Scope _scope;
    private AggregateFrame? _aggregate;
    /// <summary>Measures: inside an aggregate over the group's rows, where row values may be used.</summary>
    private bool _inGroup;
    /// <summary>The fraction PERCENTILE asked for while its call binds (MEDIAN: 0.5).</summary>
    private double _percentile = 0.5;
    private readonly List<FormulaError> _errors = [];
    private readonly List<string> _columns = [];

    private bool _pg => _shared.Pg;

    private FormulaCompiler(Shared shared, Scope scope)
    {
        _shared = shared;
        _root = scope;
        _scope = scope;
    }

    /// <param name="alias">The SQL alias (or quoted table name) of the row: <c>t."amount"</c>.</param>
    /// <param name="expected">The declared result type; a formula that cannot produce it is an error.</param>
    /// <param name="column">The computed column the formula belongs to, so using itself is a cycle.</param>
    public static FormulaCheck Compile(
        string formula,
        TableDef table,
        FormulaSchema schema,
        DatabaseProvider provider,
        string alias = "t",
        FormulaType? expected = null,
        string? column = null,
        FormulaContext? context = null)
    {
        var shared = new Shared(schema, provider == DatabaseProvider.Postgres, context ?? new FormulaContext());
        if (column is not null)
            shared.Chain.Add($"{table.Name}.{column}");
        return new FormulaCompiler(shared, new Scope(table, alias)).Run(formula, expected, column);
    }

    /// <summary>
    /// A measure over the rows of a group (a widget's value per month, per
    /// category, …). <c>RUNNING_SUM(…)</c> around the whole formula accumulates
    /// it over the date group.
    /// </summary>
    public static MeasureCheck CompileMeasure(
        string formula,
        TableDef table,
        FormulaSchema schema,
        DatabaseProvider provider,
        string alias = "t",
        FormulaContext? context = null)
    {
        var shared = new Shared(schema, provider == DatabaseProvider.Postgres, (context ?? new FormulaContext()) with { Measure = true });
        var compiler = new FormulaCompiler(shared, new Scope(table, alias));
        FormulaNode root;
        try
        {
            root = FormulaParser.Parse(formula ?? "");
        }
        catch (FormulaSyntaxException e)
        {
            return new MeasureCheck(new FormulaCheck(null, [e.Error]), false);
        }

        var running = root is FunctionCall { Arguments.Count: 1, HasWhere: false } call &&
                      string.Equals(call.Name, "RUNNING_SUM", StringComparison.OrdinalIgnoreCase);
        var check = compiler.RunNode(running ? ((FunctionCall)root).Arguments[0] : root, (formula ?? "").Length, null, null);
        if (running && check.Result?.Custom is not null)
            check = new FormulaCheck(null, [new FormulaError("RUNNING_SUM cannot add up a CUSTOM(…) value yet; add up its inputs instead.", root.Start, root.End)]);
        if (running && check.Result?.Series is not null)
            check = new FormulaCheck(null, [new FormulaError("RUNNING_SUM cannot add up a return; TWR(…) already accumulates over the periods.", root.Start, root.End)]);
        if (running && check.Result is { } result && !FormulaTypes.IsNumeric(result.Type))
            check = new FormulaCheck(null, [new FormulaError($"RUNNING_SUM needs a number, not {FormulaTypes.Describe(result.Type)}.", root.Start, root.End)]);
        return new MeasureCheck(check, running);
    }

    /// <param name="stored">The computed column the formula belongs to: there, CUSTOM(…) reads the stored value.</param>
    private FormulaCheck Run(string? formula, FormulaType? expected, string? stored = null)
    {
        FormulaNode root;
        try
        {
            root = FormulaParser.Parse(formula ?? "");
        }
        catch (FormulaSyntaxException e)
        {
            return new FormulaCheck(null, [e.Error]);
        }
        return RunNode(root, (formula ?? "").Length, expected, stored);
    }

    private FormulaCheck RunNode(FormulaNode root, int length, FormulaType? expected, string? stored)
    {
        if (root is FunctionCall call && IsCustom(call.Name))
            return RunCustom(call, length, expected, stored);
        if (root is FunctionCall series && IsSeries(series.Name))
            return RunSeries(series);

        var expr = Bind(root);
        if (expr is null || _errors.Count > 0)
            return new FormulaCheck(null, _errors);
        if (expr.IsRows)
            return new FormulaCheck(null, [new FormulaError("A formula gives one value; count the rows with COUNT(…).", root.Start, root.End)]);

        var type = expr.Type;
        var sql = expr.Sql;
        if (expected is { } target && target != type)
        {
            if (!Fits(type, target))
                return new FormulaCheck(null, [new FormulaError(
                    $"The formula gives {FormulaTypes.Describe(type)}, but the column holds {FormulaTypes.Describe(target)}.", 0, length)]);
            sql = Convert(expr, target);
            type = target;
        }
        if (type == FormulaType.Null)
            type = expected ?? FormulaType.Text;

        return new FormulaCheck(new CompiledFormula(sql, type, _columns), []);
    }

    private static bool Fits(FormulaType actual, FormulaType expected) =>
        actual == FormulaType.Null ||
        (expected is FormulaType.Number or FormulaType.Currency && FormulaTypes.IsNumeric(actual)) ||
        (expected == FormulaType.DateTime && actual == FormulaType.Date);

    // ── Custom functions (Step 4.1) ──────────────────────────────────────

    private static bool IsCustom(string name) => string.Equals(name, "CUSTOM", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name of the table that keeps computed columns' CUSTOM values (in the data database).</summary>
    public const string StoredValuesTable = "_bento_function_values";

    /// <summary>
    /// <c>CUSTOM("name", arg, …)</c> as the whole formula. The function runs
    /// in the sandbox, not in SQL: a computed column reads the value stored by
    /// the background recalculation (Tier 3); measures and variables run the
    /// function on each result row. The arguments are checked and compiled
    /// here, so callers only call the function.
    /// </summary>
    private FormulaCheck RunCustom(FunctionCall call, int length, FormulaType? expected, string? stored)
    {
        if (call.HasWhere)
            return new FormulaCheck(null, [new FormulaError("WHERE does not work in CUSTOM; put the condition into an argument, e.g. SUM(amount WHERE amount > 0).", call.Start, call.End)]);
        if (call.Arguments.Count == 0 || call.Arguments[0] is not StringLiteral nameLiteral)
            return new FormulaCheck(null, [new FormulaError("CUSTOM needs the function's name as text first: CUSTOM(\"risk_class\", volatility).",
                call.Arguments.Count > 0 ? call.Arguments[0].Start : call.Start, call.Arguments.Count > 0 ? call.Arguments[0].End : call.End)]);

        var functions = _shared.Context.Functions?.Functions ?? StaticFunctionCatalog.Empty.Functions;
        if (!functions.TryGetValue(nameLiteral.Value.Trim(), out var function))
        {
            var suggestion = Suggest(nameLiteral.Value, functions.Keys);
            return new FormulaCheck(null, [new FormulaError(
                functions.Count == 0
                    ? $"There is no custom function '{nameLiteral.Value}'; create one under Functions first."
                    : $"There is no custom function '{nameLiteral.Value}'" + (suggestion is null ? "." : $" — did you mean '{suggestion}'?"),
                nameLiteral.Start, nameLiteral.End)]);
        }

        if (!function.UsableInFormulas)
            return new FormulaCheck(null, [new FormulaError(function.UsesNetwork
                ? $"{function.Name} calls the internet; such functions run as price sources, not in formulas."
                : $"{function.Name} gives any JSON value; formulas need a function with a declared result type.",
                nameLiteral.Start, nameLiteral.End)]);

        var given = call.Arguments.Count - 1;
        if (given != function.Arguments.Count)
            return new FormulaCheck(null, [new FormulaError(
                $"{function.Name} takes {Count(function.Arguments.Count, "argument")} ({string.Join(", ", function.Arguments.Select(a => a.Name))}), not {given}.",
                call.Start, call.End)]);

        var arguments = new List<CustomArgument>();
        foreach (var (node, index) in call.Arguments.Skip(1).Select((n, i) => (n, i)))
        {
            var bound = Bind(node);
            if (bound is null)
                continue;
            var declared = function.Arguments[index];
            if (bound.IsRows)
                Fail(node, $"'{declared.Name}' gets one value; count the rows with COUNT(…).");
            else if (!CustomFunctionRules.Accepts(declared.Type, bound.Type))
                Fail(node, $"'{declared.Name}' of {function.Name} takes {DescribeArgument(declared.Type)}, not {FormulaTypes.Describe(bound.Type)}.");
            else
                arguments.Add(new CustomArgument(bound.Sql, bound.Type));
        }
        if (_errors.Count > 0)
            return new FormulaCheck(null, _errors);

        var type = function.Result;
        var expr = new Expr("NULL", type, call);
        if (stored is not null)
        {
            var table = _scope.Table;
            var key = table.Columns.FirstOrDefault(c => c.PrimaryKey);
            if (key is null)
                return new FormulaCheck(null, [new FormulaError($"CUSTOM in a computed column needs a primary key on {table.DisplayName ?? table.Name}.", call.Start, call.End)]);
            var value = $"(SELECT v.value FROM {Q(StoredValuesTable)} v WHERE v.table_name = {Str(table.Name)} AND v.column_name = {Str(stored)} " +
                        $"AND v.row_key = CAST({_scope.Alias}.{Q(key.Name)} AS TEXT))";
            expr = expr with { Sql = CustomFunctionValues.ReadSql(value, type, _pg) };
        }

        var sql = expr.Sql;
        if (expected is { } target && target != type)
        {
            if (!Fits(type, target))
                return new FormulaCheck(null, [new FormulaError(
                    $"{function.Name} gives {FormulaTypes.Describe(type)}, but the column holds {FormulaTypes.Describe(target)}.", 0, length)]);
            sql = Convert(expr, target);
            type = target;
        }
        return new FormulaCheck(new CompiledFormula(sql, type, _columns, new CustomCall(function, arguments)), []);
    }

    private static string DescribeArgument(string type) =>
        type == "any" ? "any value" : FormulaTypes.FromName(type) is { } known ? FormulaTypes.Describe(known) : type;

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // ── Returns (Step 4.3) ───────────────────────────────────────────────

    private static readonly HashSet<string> SeriesFunctions = new(StringComparer.OrdinalIgnoreCase) { "XIRR", "IRR", "TWR", "REBASE" };

    private static bool IsSeries(string name) => SeriesFunctions.Contains(name);

    /// <summary>
    /// XIRR/IRR/TWR/REBASE as the whole measure. Each part is a measure (or, for
    /// XIRR/IRR, FLOWS(amount, date) over many rows) the database computes per
    /// result row; the return itself is computed afterwards (<see cref="Returns"/>).
    /// </summary>
    private FormulaCheck RunSeries(FunctionCall call)
    {
        var name = call.Name.ToUpperInvariant();
        if (!_shared.Context.Measure)
            return new FormulaCheck(null, [new FormulaError($"{name} works in dashboard measures and variables, over the rows of a table.", call.Start, call.End)]);

        var parts = new List<SeriesPart>();
        decimal? @base = null;
        switch (name)
        {
            case "XIRR" or "IRR":
            {
                var args = call.Arguments.ToList();
                if (args.Count == 0)
                    return Error(call, $"Write {name}(amount, date) over the rows, or {name}(FLOWS(amount, date), …, final_value, final_date).");
                // Shorthand: XIRR(amount, date [WHERE condition], …) — the first two are the flows of the group's rows.
                if (args[0] is not FunctionCall { Name: var first } || !string.Equals(first, "FLOWS", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Count < 2)
                        return Error(call, $"Write {name}(amount, date): the cash flows (paid out negative) and their dates.");
                    var where = call.HasWhere ? args[^1] : null;
                    if (where is not null && args.Count != 3)
                        return Error(call, $"A condition goes into FLOWS when {name} has more parts: {name}(FLOWS(amount, date WHERE condition), …).");
                    var flows = new FunctionCall("FLOWS", where is null ? [args[0], args[1]] : [args[0], args[1], where], call.NameEnd, args[0].Start, args[1].End) { HasWhere = where is not null };
                    args = [flows, .. args.Skip(2).Take(where is null ? int.MaxValue : 0)];
                }
                else if (call.HasWhere)
                {
                    return Error(call, $"Put the condition into FLOWS: {name}(FLOWS(amount, date WHERE condition), …).");
                }

                for (var i = 0; i < args.Count; i++)
                {
                    if (args[i] is FunctionCall f && string.Equals(f.Name, "FLOWS", StringComparison.OrdinalIgnoreCase))
                    {
                        if (BindFlows(f) is { } sql)
                            parts.Add(new SeriesPart("flows", sql));
                        continue;
                    }
                    if (i + 1 >= args.Count)
                        return Error(args[i], "A single flow is a pair: an amount and its date, e.g. SUM(value), TODAY().");
                    var amount = Bind(args[i]);
                    var date = Bind(args[i + 1]);
                    i++;
                    if (amount is null || date is null)
                        continue;
                    if (!RequireNumber(amount, $"The amount of {name}"))
                        continue;
                    var dateText = date.Node is StringLiteral literal &&
                                   DateOnly.TryParseExact(literal.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                    if (!FormulaTypes.IsTemporal(date.Type) && date.Type != FormulaType.Null && !dateText)
                    {
                        Fail(date.Node, $"The date of a flow must be a date (or \"yyyy-MM-dd\"), not {FormulaTypes.Describe(date.Type)}.");
                        continue;
                    }
                    parts.Add(new SeriesPart("amount", amount.Sql));
                    parts.Add(new SeriesPart("date", date.Sql));
                }
                break;
            }
            case "TWR":
            {
                if (call.HasWhere || call.Arguments.Count is < 2 or > 3)
                    return Error(call, "Write TWR(value, flow [, 100]): the value at each period end and the money put in during the period (taken out: negative).");
                var value = Bind(call.Arguments[0]);
                var flow = Bind(call.Arguments[1]);
                if (value is not null && RequireNumber(value, "The value of TWR"))
                    parts.Add(new SeriesPart("value", value.Sql));
                if (flow is not null && RequireNumber(flow, "The flow of TWR"))
                    parts.Add(new SeriesPart("flow", flow.Sql));
                if (call.Arguments.Count == 3)
                {
                    if (call.Arguments[2] is not NumberLiteral { Value: > 0 } start)
                        return Error(call.Arguments[2], "The index starts at a positive number, e.g. TWR(value, flow, 100).");
                    @base = start.Value;
                }
                break;
            }
            default: // REBASE
            {
                if (call.HasWhere || call.Arguments.Count is < 1 or > 2)
                    return Error(call, "Write REBASE(measure [, 100]): the measure relative to its first value in the range.");
                var value = Bind(call.Arguments[0]);
                if (value is not null && RequireNumber(value, "REBASE"))
                    parts.Add(new SeriesPart("value", value.Sql));
                if (call.Arguments.Count == 2)
                {
                    if (call.Arguments[1] is not NumberLiteral { Value: > 0 } start)
                        return Error(call.Arguments[1], "The first value becomes a positive number, e.g. REBASE(SUM(value), 100).");
                    @base = start.Value;
                }
                @base ??= 100;
                break;
            }
        }
        if (_errors.Count > 0)
            return new FormulaCheck(null, _errors);
        if (name is "XIRR" or "IRR" && parts.Count == 0)
            return Error(call, $"{name} needs cash flows.");
        return new FormulaCheck(new CompiledFormula("NULL", FormulaType.Number, _columns, Series: new SeriesCall(name.ToLowerInvariant(), parts, @base)), []);
    }

    private FormulaCheck Error(FormulaNode node, string message) => new(null, [new FormulaError(message, node.Start, node.End)]);

    /// <summary>
    /// FLOWS(amount, date [WHERE condition]): the cash flows of the group's rows,
    /// or of each row's related rows (FLOWS(trades.amount, trades.date)), as
    /// "amount|date;…" text for <see cref="Returns"/>.
    /// </summary>
    private string? BindFlows(FunctionCall call)
    {
        var valueCount = call.Arguments.Count - (call.HasWhere ? 1 : 0);
        if (valueCount != 2)
        {
            Fail(call, "Write FLOWS(amount, date) or FLOWS(amount, date WHERE condition); money paid out is negative.");
            return null;
        }

        var frame = new AggregateFrame(_scope);
        var outerAggregate = _aggregate;
        var outerGroup = _inGroup;
        _aggregate = frame;
        _inGroup = true;
        var amount = Bind(call.Arguments[0]);
        var date = Bind(call.Arguments[1]);
        var condition = call.HasWhere ? Bind(call.Arguments[^1]) : null;
        _aggregate = outerAggregate;
        _inGroup = outerGroup;
        if (amount is null || date is null || (call.HasWhere && condition is null))
            return null;
        if (!RequireNumber(amount, "The amount of FLOWS"))
            return null;
        if (!FormulaTypes.IsTemporal(date.Type))
        {
            Fail(date.Node, $"The date of FLOWS must be a date, not {FormulaTypes.Describe(date.Type)}.");
            return null;
        }
        if (condition is not null && !RequireBoolean(condition, "The condition of FLOWS"))
            return null;

        var text = $"CAST({amount.Sql} AS TEXT) || '|' || {(_pg ? $"CAST({date.Sql} AS TEXT)" : date.Sql)}";
        if (condition is not null)
            text = $"CASE WHEN {condition.Sql} THEN {text} END";
        string Concat(string value) => _pg ? $"STRING_AGG({value}, ';')" : $"GROUP_CONCAT({value}, ';')";

        if (frame.Navigation is null)
            return Concat(text);

        // The related rows of each group row, then all of them.
        var navigation = frame.Navigation;
        var rows = frame.Rows!;
        var key = _scope.Table.Columns.FirstOrDefault(c => c.PrimaryKey);
        if (key is null || navigation.Kind != NavigationKind.Children)
        {
            Fail(call, "FLOWS follows links from a table to its rows in another, e.g. FLOWS(trades.amount, trades.date) on accounts.");
            return null;
        }
        var related = $"(SELECT {Concat(text)} FROM {Q(rows.Table.Name)} {rows.Alias} WHERE {rows.Alias}.{Q(navigation.ForeignKey!)} = {_scope.Alias}.{Q(key.Name)})";
        return Concat(related);
    }

    // ── Binding ──────────────────────────────────────────────────────────

    private Expr? Bind(FormulaNode node) => node switch
    {
        NumberLiteral n => new Expr(NumberSql(n.Value, n.IsInteger), n.IsInteger ? FormulaType.Integer : FormulaType.Number, n, n.Value),
        StringLiteral s => new Expr(Str(s.Value), FormulaType.Text, s, s.Value),
        BooleanLiteral b => new Expr(b.Value ? "TRUE" : "FALSE", FormulaType.Boolean, b, b.Value),
        NullLiteral => new Expr("NULL", FormulaType.Null, node),
        ColumnReference c => BindColumn(c),
        UnaryExpression u => BindUnary(u),
        BinaryExpression b => BindBinary(b),
        FunctionCall f => BindCall(f),
        _ => throw new InvalidOperationException($"Unknown node {node.GetType().Name}")
    };

    private Expr? BindColumn(ColumnReference reference)
    {
        var table = _scope.Table;
        var name = reference.Path[0];
        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? table.Columns.FirstOrDefault(c => string.Equals(c.DisplayName?.Trim(), name, StringComparison.OrdinalIgnoreCase));

        if (column is null && reference.Path.Count == 1 && !reference.Bracketed &&
            _shared.Context.Variables?.FirstOrDefault(v => string.Equals(v.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } variable)
            return Constant(variable.Value, reference);

        // A measure sees the group, not single rows: row values only inside an aggregate.
        if (_shared.Context.Measure && !_inGroup && ReferenceEquals(_scope, _root) &&
            (column is not null || _shared.Schema.FindNavigation(table, name) is not null))
            return Fail(reference, $"'{string.Join(".", reference.Path)}' differs from row to row; use it inside SUM(…), AVG(…), MIN(…), MAX(…) or COUNT(…).");

        if (reference.Path.Count > 1)
            return BindPath(reference, column);

        if (column is null)
        {
            var navigation = _shared.Schema.FindNavigation(table, name);
            if (navigation is not null && navigation.ToMany)
                return BindRows(reference, navigation, []);

            var candidates = table.Columns.Where(c => c.Type != ColumnType.Json).Select(c => c.Name);
            var suggestion = Suggest(name, candidates);
            if (suggestion is null && FormulaFunctions.Find(name) is not null)
                return Fail(reference, $"{name.ToUpperInvariant()} is a function; call it with parentheses: {name.ToUpperInvariant()}(…).");
            return Fail(reference, $"Column '{name}' not found in {table.DisplayName ?? table.Name}" +
                (suggestion is null ? "." : $" — did you mean '{suggestion}'?"));
        }

        if (column.Type == ColumnType.Computed)
            return BindComputed(reference, column);
        if (FormulaTypes.FromColumn(column.Type) is not { } type)
            return Fail(reference, $"'{column.Name}' is a {column.Type.ToString().ToLowerInvariant()} column; formulas cannot use those.");

        if (ReferenceEquals(_scope, _root) && !_columns.Contains(column.Name, StringComparer.Ordinal))
            _columns.Add(column.Name);

        var sql = $"{_scope.Alias}.{Q(column.Name)}";
        var textKey = column.Type == ColumnType.Uuid ||
            (column.Type == ColumnType.Reference && column.References is not null &&
             _shared.Schema.Table(column.References)?.Columns.FirstOrDefault(c => c.PrimaryKey)?.Type == ColumnType.Uuid);
        if (textKey)
        {
            type = FormulaType.Text;
            if (_pg)
                sql = $"CAST({sql} AS TEXT)";
        }
        else if (type == FormulaType.Date)
        {
            sql = _pg ? sql : $"date({sql})";
        }
        else if (type == FormulaType.DateTime)
        {
            sql = _pg ? $"({sql} AT TIME ZONE 'UTC')" : $"datetime({sql})";
        }
        return new Expr(sql, type, reference);
    }

    /// <summary>Another computed column: its formula inlined, cycles reported.</summary>
    private Expr? BindComputed(ColumnReference reference, ColumnDef column)
    {
        var key = $"{_scope.Table.Name}.{column.Name}";
        var start = _shared.Chain.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (start >= 0)
            return Fail(reference, $"Circular reference: {string.Join(" → ", _shared.Chain.Skip(start).Append(key))}.");

        if (ReferenceEquals(_scope, _root) && !_columns.Contains(column.Name, StringComparer.Ordinal))
            _columns.Add(column.Name);

        _shared.Chain.Add(key);
        // A computed column is a row formula, also inside a measure.
        var check = new FormulaCompiler(_shared, _scope) { _inGroup = true }.Run(column.Formula, FormulaTypes.FromName(column.ResultType), column.Name);
        _shared.Chain.RemoveAt(_shared.Chain.Count - 1);

        if (!check.IsValid)
        {
            var problem = check.Errors.FirstOrDefault()?.Message ?? "it does not check";
            return problem.StartsWith("Circular reference", StringComparison.Ordinal)
                ? Fail(reference, problem)
                : Fail(reference, $"'{column.Name}' has an error: {problem}");
        }
        return new Expr($"({check.Result!.Sql})", check.Result.Type, reference);
    }

    /// <summary><c>link.column</c>: a value of the referenced row, or related rows inside an aggregate.</summary>
    private Expr? BindPath(ColumnReference reference, ColumnDef? column)
    {
        var table = _scope.Table;
        var name = reference.Path[0];
        var navigation = _shared.Schema.FindNavigation(table, name);
        if (navigation is null)
        {
            if (column is not null)
                return Fail(reference, $"'{column.Name}' is not a link; only reference columns and relations can be followed with a dot.");
            var names = _shared.Schema.Navigations(table).SelectMany(n => n.Names);
            var suggestion = Suggest(name, names);
            return Fail(reference, $"'{name}' is not a column or link of {table.DisplayName ?? table.Name}" +
                (suggestion is null ? "." : $" — did you mean '{suggestion}'?"));
        }

        var rest = reference with { Path = reference.Path.Skip(1).ToList() };
        if (navigation.ToMany)
            return BindRows(reference, navigation, rest.Path);

        var target = _shared.Schema.Table(navigation.Target)!;
        var key = target.Columns.FirstOrDefault(c => c.PrimaryKey);
        if (key is null)
            return Fail(reference, $"'{target.Name}' has no key to look rows up by.");

        var alias = _shared.NextAlias("p");
        var outer = _scope;
        var frame = _aggregate;
        _scope = new Scope(target, alias);
        // A lookup from inside an aggregate stays inside it; links to several rows end there.
        _aggregate = null;
        var value = BindColumn(rest);
        _scope = outer;
        _aggregate = frame;
        if (value is null)
            return null;
        if (value.IsRows)
            return Fail(reference, "A link to several rows can only be used directly in SUM, COUNT, AVG, MIN or MAX.");

        return value with
        {
            Sql = $"(SELECT {value.Sql} FROM {Q(target.Name)} {alias} WHERE {alias}.{Q(key.Name)} = {outer.Alias}.{Q(navigation.ForeignKey!)})",
            Node = reference
        };
    }

    /// <summary>Related rows (<c>transactions</c>, <c>transactions.amount</c>) inside an aggregate.</summary>
    private Expr? BindRows(ColumnReference reference, FormulaNavigation navigation, IReadOnlyList<string> rest)
    {
        var display = string.Join(".", reference.Path);
        if (_aggregate is null && _shared.Context.Measure && _inGroup && ReferenceEquals(_scope, _root))
            return Fail(reference, $"In a measure, SUM(…) adds up the rows of the group; for the related rows of each row nest it: SUM(SUM({(rest.Count > 0 ? display : reference.Path[0] + ".column")})).");
        if (_aggregate is null)
            return Fail(reference, $"'{reference.Path[0]}' holds several rows; use SUM({(rest.Count > 0 ? display : reference.Path[0] + ".column")}), COUNT({reference.Path[0]}), AVG, MIN or MAX.");
        if (!ReferenceEquals(_aggregate.Outer, _scope))
            return Fail(reference, "Related rows of related rows cannot be aggregated; add a computed column on the related table and use that.");
        if (_aggregate.Navigation is { } used && !ReferenceEquals(used, navigation))
            return Fail(reference, $"One aggregate can use one link; this one already uses '{used.Name}'.");

        var target = _shared.Schema.Table(navigation.Target)!;
        _aggregate.Navigation = navigation;
        _aggregate.Rows ??= new Scope(target, _shared.NextAlias("r"));
        if (rest.Count == 0)
            return new Expr("*", FormulaType.Null, reference, IsRows: true);

        var outer = _scope;
        var frame = _aggregate;
        _scope = frame.Rows;
        _aggregate = null;
        var value = BindColumn(reference with { Path = rest });
        _scope = outer;
        _aggregate = frame;
        return value is null ? null : value with { Node = reference };
    }

    private Expr? BindUnary(UnaryExpression unary)
    {
        var operand = Bind(unary.Operand);
        if (operand is null)
            return null;

        if (unary.Operator == "NOT")
            return RequireBoolean(operand, "NOT") ? new Expr($"(NOT {operand.Sql})", FormulaType.Boolean, unary) : null;

        if (operand.Type == FormulaType.Null)
            return new Expr("NULL", FormulaType.Null, unary);
        if (!FormulaTypes.IsNumeric(operand.Type))
            return Fail(unary, $"Only numbers can be negated, not {FormulaTypes.Describe(operand.Type)}.");
        return new Expr($"(-{operand.Sql})", operand.Type, unary);
    }

    private Expr? BindBinary(BinaryExpression binary)
    {
        var left = Bind(binary.Left);
        var right = Bind(binary.Right);
        if (left is null || right is null)
            return null;

        switch (binary.Operator)
        {
            case "AND" or "OR":
                if (!RequireBoolean(left, binary.Operator) | !RequireBoolean(right, binary.Operator))
                    return null;
                return new Expr($"({left.Sql} {binary.Operator} {right.Sql})", FormulaType.Boolean, binary);

            case "&":
                return new Expr($"({TextOrEmpty(left)} || {TextOrEmpty(right)})", FormulaType.Text, binary);

            case "=" or "<>" or "<" or "<=" or ">" or ">=":
                return Compare(left, binary.Operator, right, binary);

            default:
                return Arithmetic(binary.Operator, left, right, binary);
        }
    }

    // ── Operators ────────────────────────────────────────────────────────

    private Expr? Arithmetic(string op, Expr left, Expr right, FormulaNode node)
    {
        // Dates: date ± days, date - date = days.
        if (op is "+" or "-" && (FormulaTypes.IsTemporal(left.Type) || FormulaTypes.IsTemporal(right.Type)))
        {
            if (FormulaTypes.IsTemporal(left.Type) && right.Type is FormulaType.Integer or FormulaType.Null)
                return new Expr(DateAdd(left, op == "-" ? $"(-{right.Sql})" : right.Sql, "days"), left.Type, node);
            if (op == "+" && FormulaTypes.IsTemporal(right.Type) && left.Type == FormulaType.Integer)
                return new Expr(DateAdd(right, left.Sql, "days"), right.Type, node);
            if (op == "-" && FormulaTypes.IsTemporal(left.Type) && FormulaTypes.IsTemporal(right.Type))
                return new Expr(DateDiff(right, left, "days"), FormulaType.Integer, node);
            return Fail(node, op == "+"
                ? "Dates can only be added to a whole number of days; use DATEADD for months or years."
                : "From a date you can subtract a whole number of days or another date.");
        }

        var symbol = op switch { "^" => "^ (power)", "%" => "% (remainder)", _ => op };
        if (!RequireNumber(left, symbol) | !RequireNumber(right, symbol))
            return null;

        var anyCurrency = left.Type == FormulaType.Currency || right.Type == FormulaType.Currency;
        var anyNumber = left.Type == FormulaType.Number || right.Type == FormulaType.Number;
        switch (op)
        {
            case "+" or "-":
                return new Expr($"({left.Sql} {op} {right.Sql})",
                    anyCurrency ? FormulaType.Currency : anyNumber ? FormulaType.Number : Widest(left.Type, right.Type), node);
            case "*":
                var product = left.Type == FormulaType.Currency && right.Type == FormulaType.Currency ? FormulaType.Number
                    : anyCurrency ? FormulaType.Currency : anyNumber ? FormulaType.Number : Widest(left.Type, right.Type);
                return new Expr($"({left.Sql} * {right.Sql})", product, node);
            case "/":
                var quotient = left.Type == FormulaType.Currency && right.Type != FormulaType.Currency ? FormulaType.Currency : FormulaType.Number;
                return new Expr(Divide(left.Sql, right.Sql), quotient, node);
            case "%":
                if (!RequireInteger(left, "% (remainder)") | !RequireInteger(right, "% (remainder)"))
                    return null;
                return new Expr($"({left.Sql} % NULLIF({right.Sql}, 0))", FormulaType.Integer, node);
            case "^":
                return new Expr(Power(left.Sql, right.Sql), FormulaType.Number, node);
        }
        throw new InvalidOperationException($"Unknown operator {op}");
    }

    private static FormulaType Widest(FormulaType a, FormulaType b) =>
        a == FormulaType.Null ? (b == FormulaType.Null ? FormulaType.Integer : b) : a;

    private Expr? Compare(Expr left, string op, Expr right, FormulaNode node)
    {
        if (left.Type == FormulaType.Null || right.Type == FormulaType.Null)
        {
            var other = left.Type == FormulaType.Null ? right : left;
            return op switch
            {
                "=" => new Expr($"({other.Sql} IS NULL)", FormulaType.Boolean, node),
                "<>" => new Expr($"({other.Sql} IS NOT NULL)", FormulaType.Boolean, node),
                _ => Fail(node, "Empty values cannot be ordered; test them with ISBLANK(…).")
            };
        }

        if (FormulaTypes.IsNumeric(left.Type) && FormulaTypes.IsNumeric(right.Type))
            return new Expr($"({Round10(left)} {op} {Round10(right)})", FormulaType.Boolean, node);

        if (left.Type == FormulaType.Text && right.Type == FormulaType.Text)
        {
            var collate = _pg && op is not ("=" or "<>") ? " COLLATE \"C\"" : "";
            return new Expr($"({LowerFor(left)}{collate} {op} {LowerFor(right)}{collate})", FormulaType.Boolean, node);
        }

        if (FormulaTypes.IsTemporal(left.Type) || FormulaTypes.IsTemporal(right.Type))
        {
            var target = left.Type == FormulaType.DateTime || right.Type == FormulaType.DateTime ? FormulaType.DateTime : FormulaType.Date;
            var l = AsTemporal(left, target);
            var r = AsTemporal(right, target);
            if (l is null || r is null)
                return Fail(node, $"Cannot compare {FormulaTypes.Describe(left.Type)} with {FormulaTypes.Describe(right.Type)}.");
            return new Expr($"({l} {op} {r})", FormulaType.Boolean, node);
        }

        if (left.Type == FormulaType.Boolean && right.Type == FormulaType.Boolean)
        {
            if (op is not ("=" or "<>"))
                return Fail(node, "Yes/no values can only be compared with = or <>.");
            return new Expr($"({left.Sql} {op} {right.Sql})", FormulaType.Boolean, node);
        }

        return Fail(node, $"Cannot compare {FormulaTypes.Describe(left.Type)} with {FormulaTypes.Describe(right.Type)}.");
    }

    // ── Functions ────────────────────────────────────────────────────────

    private Expr? BindCall(FunctionCall call)
    {
        var info = FormulaFunctions.Find(call.Name);
        if (info is null)
        {
            var suggestion = Suggest(call.Name, FormulaFunctions.Catalog.Select(f => f.Name));
            return Fail(call.Start, call.NameEnd, $"Unknown function {call.Name.ToUpperInvariant()}" +
                (suggestion is null ? "." : $" — did you mean {suggestion}?"));
        }

        var name = info.Name;
        if (name == "CUSTOM")
            return Fail(call, "CUSTOM(…) must be the whole formula. Use its result in another computed column or dashboard variable.");
        if (IsSeries(name))
            return Fail(call, $"{name}(…) must be the whole formula of a dashboard measure; show it as a percentage in the widget.");
        if (name == "FLOWS")
            return Fail(call, "FLOWS(amount, date) is an argument of XIRR or IRR: XIRR(FLOWS(-invested, date), SUM(value), TODAY()).");
        if (name == "PERCENTILE")
            return BindPercentile(call);
        if (info.Category == FormulaFunctions.RelatedRows)
            return _shared.Context.Measure && !_inGroup ? BindGroupAggregate(call, name) : BindAggregate(call, name);
        if (call.HasWhere)
            return Fail(call, $"WHERE only works inside SUM, COUNT, AVG, MIN, MAX, MEDIAN or PERCENTILE, not in {name}.");
        if (name == "RUNNING_SUM")
            return Fail(call, "RUNNING_SUM wraps a whole measure: RUNNING_SUM(SUM(amount)).");
        if (name == "LOOKUP_ASOF")
            return BindLookupAsOf(call);

        var args = new List<Expr>();
        var failed = false;
        foreach (var argument in call.Arguments)
        {
            var bound = Bind(argument);
            if (bound is null)
                failed = true;
            else
                args.Add(bound);
        }
        if (failed)
            return null;

        switch (name)
        {
            // Logic
            case "IF":
            {
                if (!Arity(call, 2, 3) || !RequireBoolean(args[0], "IF"))
                    return null;
                var branches = args.Skip(1).ToList();
                if (Unify(branches, call, "IF") is not { } type)
                    return null;
                var otherwise = branches.Count > 1 ? $" ELSE {Convert(branches[1], type)}" : "";
                return new Expr($"(CASE WHEN {args[0].Sql} THEN {Convert(branches[0], type)}{otherwise} END)", type, call);
            }
            case "AND" or "OR":
            {
                if (!Arity(call, 1, int.MaxValue))
                    return null;
                if (args.Select(a => RequireBoolean(a, name)).ToList().Contains(false))
                    return null;
                return new Expr("(" + string.Join($" {name} ", args.Select(a => a.Sql)) + ")", FormulaType.Boolean, call);
            }
            case "NOT":
                return Arity(call, 1, 1) && RequireBoolean(args[0], "NOT")
                    ? new Expr($"(NOT {args[0].Sql})", FormulaType.Boolean, call)
                    : null;
            case "SWITCH":
            {
                if (!Arity(call, 3, int.MaxValue))
                    return null;
                var value = args[0];
                var pairs = (args.Count - 1) / 2;
                var results = Enumerable.Range(0, pairs).Select(i => args[2 + i * 2]).ToList();
                var fallback = (args.Count - 1) % 2 == 1 ? args[^1] : null;
                if (Unify(fallback is null ? results : [.. results, fallback], call, "SWITCH") is not { } type)
                    return null;
                var whens = new List<string>();
                for (var i = 0; i < pairs; i++)
                {
                    var match = args[1 + i * 2];
                    if (Compare(value, "=", match, match.Node) is not { } test)
                        return null;
                    whens.Add($"WHEN {test.Sql} THEN {Convert(results[i], type)}");
                }
                var otherwise = fallback is null ? "" : $" ELSE {Convert(fallback, type)}";
                return new Expr($"(CASE {string.Join(" ", whens)}{otherwise} END)", type, call);
            }
            case "COALESCE":
            {
                if (!Arity(call, 1, int.MaxValue) || Unify(args, call, "COALESCE") is not { } type)
                    return null;
                return args.Count == 1
                    ? args[0] with { Node = call, Type = type }
                    : new Expr($"COALESCE({string.Join(", ", args.Select(a => Convert(a, type)))})", type, call);
            }
            case "ISBLANK":
            {
                if (!Arity(call, 1, 1))
                    return null;
                var value = args[0];
                return new Expr(value.Type switch
                {
                    FormulaType.Null => "TRUE",
                    FormulaType.Text => $"({value.Sql} IS NULL OR {value.Sql} = '')",
                    _ => $"({value.Sql} IS NULL)"
                }, FormulaType.Boolean, call);
            }

            // Math
            case "ROUND":
            {
                if (!Arity(call, 1, 2) || !RequireNumber(args[0], name))
                    return null;
                var digits = 0;
                if (args.Count == 2)
                {
                    if (args[1].Constant is not decimal d || d != decimal.Truncate(d) || d < 0 || d > 10)
                        return Fail(args[1].Node, "ROUND takes the number of decimal places as a whole number from 0 to 10.");
                    digits = (int)d;
                }
                var value = args[0];
                if (value.Type is FormulaType.Integer or FormulaType.Null)
                    return value with { Node = call, Type = value.Type == FormulaType.Null ? FormulaType.Number : value.Type };
                var sql = _pg ? $"ROUND(CAST({value.Sql} AS NUMERIC), {digits})" : $"ROUND({Round10(value)}, {digits})";
                return new Expr(sql, value.Type, call);
            }
            case "FLOOR" or "CEIL":
            {
                if (!Arity(call, 1, 1) || !RequireNumber(args[0], name))
                    return null;
                var value = args[0];
                if (value.Type == FormulaType.Integer)
                    return value with { Node = call };
                var fn = name == "FLOOR" ? "floor" : "ceil";
                return new Expr($"CAST({fn}({Round10(value)}) AS {(_pg ? "BIGINT" : "INTEGER")})", FormulaType.Integer, call);
            }
            case "ABS":
                return Arity(call, 1, 1) && RequireNumber(args[0], name)
                    ? new Expr($"abs({args[0].Sql})", args[0].Type == FormulaType.Null ? FormulaType.Number : args[0].Type, call)
                    : null;
            case "MOD":
                if (!Arity(call, 2, 2) || !RequireInteger(args[0], name) | !RequireInteger(args[1], name))
                    return null;
                return new Expr($"({args[0].Sql} % NULLIF({args[1].Sql}, 0))", FormulaType.Integer, call);
            case "POWER":
                if (!Arity(call, 2, 2) || !RequireNumber(args[0], name) | !RequireNumber(args[1], name))
                    return null;
                return new Expr(Power(args[0].Sql, args[1].Sql), FormulaType.Number, call);
            // Finance (Excel's sign convention: money paid out is negative)
            case "FV" or "PV":
            {
                if (!Arity(call, 3, 5) || args.Any(a => !RequireNumber(a, name)))
                    return null;
                var (rate, n, payment) = (Real(args[0]), Real(args[1]), Real(args[2]));
                var other = args.Count > 3 ? Real(args[3]) : "0";
                var due = args.Count > 4 ? $"(CASE WHEN {args[4].Sql} <> 0 THEN 1 ELSE 0 END)" : "0";
                var growth = $"power(1 + {rate}, {n})";
                var annuity = $"{payment} * (1 + {rate} * {due}) * ({growth} - 1) / {rate}";
                var sql = name == "FV"
                    ? $"(CASE WHEN {rate} = 0 THEN -({other} + {payment} * {n}) ELSE -({other} * {growth} + {annuity}) END)"
                    : $"(CASE WHEN {rate} = 0 THEN -({other} + {payment} * {n}) ELSE -({other} + {annuity}) / {growth} END)";
                return new Expr($"(CASE WHEN {rate} <= -1 THEN NULL ELSE {sql} END)", FormulaType.Number, call);
            }
            case "PMT":
            {
                if (!Arity(call, 3, 5) || args.Any(a => !RequireNumber(a, name)))
                    return null;
                var (rate, n, present) = (Real(args[0]), Real(args[1]), Real(args[2]));
                var future = args.Count > 3 ? Real(args[3]) : "0";
                var due = args.Count > 4 ? $"(CASE WHEN {args[4].Sql} <> 0 THEN 1 ELSE 0 END)" : "0";
                var growth = $"power(1 + {rate}, {n})";
                return new Expr(
                    $"(CASE WHEN {n} = 0 OR {rate} <= -1 THEN NULL WHEN {rate} = 0 THEN -({present} + {future}) / {n} " +
                    $"ELSE -({rate} * ({future} + {present} * {growth})) / ((1 + {rate} * {due}) * ({growth} - 1)) END)",
                    FormulaType.Number, call);
            }
            case "NPV":
            {
                if (!Arity(call, 2, int.MaxValue) || args.Any(a => !RequireNumber(a, name)))
                    return null;
                var rate = Real(args[0]);
                var terms = args.Skip(1).Select((a, i) => $"COALESCE({Real(a)}, 0) / power(1 + {rate}, {i + 1})");
                return new Expr($"(CASE WHEN {rate} <= -1 THEN NULL ELSE ({string.Join(" + ", terms)}) END)", FormulaType.Number, call);
            }
            case "SQRT":
                if (!Arity(call, 1, 1) || !RequireNumber(args[0], name))
                    return null;
                return new Expr($"(CASE WHEN {args[0].Sql} < 0 THEN NULL ELSE sqrt(CAST({args[0].Sql} AS {RealType})) END)", FormulaType.Number, call);

            // Text
            case "CONCAT":
                if (!Arity(call, 1, int.MaxValue))
                    return null;
                return new Expr("(" + string.Join(" || ", args.Select(TextOrEmpty)) + ")", FormulaType.Text, call);
            case "LEFT" or "RIGHT":
            {
                if (!Arity(call, 2, 2) || !RequireInteger(args[1], name))
                    return null;
                var text = Text(args[0]);
                var count = args[1];
                string sql;
                if (_pg)
                {
                    sql = $"{name.ToLowerInvariant()}({text}, {Int(count)})";
                    if (!(count.Constant is decimal c && c >= 0))
                        sql = $"(CASE WHEN {count.Sql} < 0 THEN '' ELSE {sql} END)";
                }
                else if (name == "LEFT")
                {
                    // substr with a length ≤ 0 is '' on SQLite.
                    sql = $"substr({text}, 1, {count.Sql})";
                }
                else
                {
                    sql = $"substr({text}, -({count.Sql}))";
                    if (!(count.Constant is decimal c && c > 0))
                        sql = $"(CASE WHEN {count.Sql} <= 0 THEN '' ELSE {sql} END)";
                }
                return new Expr(sql, FormulaType.Text, call);
            }
            case "MID":
            {
                if (!Arity(call, 3, 3) || !RequireInteger(args[1], name) | !RequireInteger(args[2], name))
                    return null;
                var (start, count) = (args[1], args[2]);
                var sql = $"substr({Text(args[0])}, {Int(start)}, {Int(count)})";
                if (!(start.Constant is decimal s && s >= 1 && count.Constant is decimal c && c >= 0))
                    sql = $"(CASE WHEN {start.Sql} < 1 OR {count.Sql} < 0 THEN NULL ELSE {sql} END)";
                return new Expr(sql, FormulaType.Text, call);
            }
            case "LEN":
                return Arity(call, 1, 1) ? new Expr($"length({Text(args[0])})", FormulaType.Integer, call) : null;
            case "UPPER" or "LOWER":
            {
                if (!Arity(call, 1, 1))
                    return null;
                var fn = _pg ? name.ToLowerInvariant() : "bento_" + name.ToLowerInvariant();
                return new Expr($"{fn}({Text(args[0])})", FormulaType.Text, call);
            }
            case "TRIM":
                return Arity(call, 1, 1) ? new Expr($"trim({Text(args[0])})", FormulaType.Text, call) : null;
            case "REPLACE":
                return Arity(call, 3, 3)
                    ? new Expr($"replace({Text(args[0])}, {Text(args[1])}, {Text(args[2])})", FormulaType.Text, call)
                    : null;
            case "SEARCH":
                return Arity(call, 2, 2) ? new Expr(Search(args[0], args[1]), FormulaType.Integer, call) : null;
            case "CONTAINS":
                return Arity(call, 2, 2) ? new Expr($"({Search(args[1], args[0])} > 0)", FormulaType.Boolean, call) : null;

            // Dates
            case "BUCKET_START" or "BUCKET_END":
            {
                if (!Arity(call, 0, 0))
                    return null;
                if (_shared.Context.BucketAlias is not { } bucket)
                    return Fail(call, $"{name}() only works in queries by period end (\"Value at the end of each period\").");
                return new Expr($"{bucket}.{Q(name == "BUCKET_START" ? "bucket" : "bucket_end")}", FormulaType.Date, call);
            }
            case "TODAY":
                return Arity(call, 0, 0)
                    ? new Expr(_pg ? "CAST((now() AT TIME ZONE 'UTC') AS DATE)" : "date('now')", FormulaType.Date, call)
                    : null;
            case "NOW":
                return Arity(call, 0, 0)
                    ? new Expr(_pg ? "date_trunc('second', now() AT TIME ZONE 'UTC')" : "datetime('now')", FormulaType.DateTime, call)
                    : null;
            case "YEAR" or "MONTH" or "DAY" or "HOUR" or "MINUTE":
            {
                if (!Arity(call, 1, 1) || RequireTemporal(args[0], name) is not { } value)
                    return null;
                return new Expr(Part(value, name), FormulaType.Integer, call);
            }
            case "DATEDIFF":
            {
                if (!Arity(call, 3, 3))
                    return null;
                var start = RequireTemporal(args[0], name);
                var end = RequireTemporal(args[1], name);
                var unit = Unit(args[2], name);
                if (start is null || end is null || unit is null)
                    return null;
                return new Expr(DateDiff(start, end, unit), FormulaType.Integer, call);
            }
            case "DATEADD":
            {
                if (!Arity(call, 3, 3))
                    return null;
                var date = RequireTemporal(args[0], name);
                var count = RequireInteger(args[1], name);
                var unit = Unit(args[2], name);
                if (date is null || !count || unit is null)
                    return null;
                return new Expr(DateAdd(date, args[1].Sql, unit), date.Type, call);
            }
        }
        throw new InvalidOperationException($"{name} is in the catalog but not implemented.");
    }

    /// <summary>
    /// SUM/AVG/MIN/MAX/COUNT over related rows, optionally with a condition
    /// (also written SUMIF/COUNTIF/AVGIF): a correlated subquery per row.
    /// </summary>
    private Expr? BindAggregate(FunctionCall call, string name)
    {
        var conditional = name.EndsWith("IF", StringComparison.Ordinal);
        var fn = conditional ? name[..^2] : name;
        if (call.HasWhere && call.Arguments.Count != 2)
            return Fail(call, $"Write {fn}(link.column WHERE condition), e.g. {fn}(transactions.amount WHERE transactions.amount < 0).");
        if (!Arity(call, conditional ? 2 : 1, 2))
            return null;
        if (_aggregate is not null)
            return Fail(call, $"{name} cannot be used inside another aggregate.");

        var frame = new AggregateFrame(_scope);
        _aggregate = frame;
        var args = call.Arguments.Select(Bind).ToList();
        _aggregate = null;
        if (args.Any(a => a is null))
            return null;

        if (frame.Navigation is null || frame.Rows is null)
            return Fail(call, $"{name} adds up related rows; name the link, e.g. {fn}(transactions{(fn == "COUNT" ? "" : ".amount")}).");

        var value = args[0]!;
        var condition = args.Count > 1 ? args[1] : null;
        if (condition is not null && (condition.IsRows || !RequireBoolean(condition, $"The condition of {name}")))
            return condition.IsRows ? Fail(condition.Node, $"The condition of {name} needs yes/no, e.g. transactions.amount < 0.") : null;
        if (value.IsRows && fn != "COUNT")
            return Fail(value.Node, $"{name} needs a column of the related rows, e.g. {fn}({value.Node switch { ColumnReference r => string.Join(".", r.Path), _ => "transactions" }}.amount).");

        string aggregate;
        FormulaType type;
        switch (fn)
        {
            case "COUNT":
                aggregate = value.IsRows ? "COUNT(*)" : $"COUNT({value.Sql})";
                type = FormulaType.Integer;
                break;
            case "SUM" or "AVG":
                if (!RequireNumber(value, name))
                    return null;
                type = fn == "AVG"
                    ? value.Type == FormulaType.Currency ? FormulaType.Currency : FormulaType.Number
                    : value.Type == FormulaType.Null ? FormulaType.Number : value.Type;
                aggregate = $"{fn}({value.Sql})";
                break;
            case "MEDIAN" or "PERCENTILE":
                if (!RequireNumber(value, name))
                    return null;
                type = value.Type == FormulaType.Currency ? FormulaType.Currency : FormulaType.Number;
                aggregate = PercentileSql(value.Sql, fn == "MEDIAN" ? 0.5 : _percentile, _pg);
                break;
            default:
                if (!(FormulaTypes.IsNumeric(value.Type) || FormulaTypes.IsTemporal(value.Type)))
                    return Fail(value.Node, $"{name} needs numbers or dates, not {FormulaTypes.Describe(value.Type)}.");
                type = value.Type;
                aggregate = $"{fn}({value.Sql})";
                break;
        }

        // Postgres sums whole numbers as NUMERIC; SQLite as INTEGER.
        if (type == FormulaType.Integer && fn != "COUNT")
            aggregate = $"CAST({aggregate} AS {(_pg ? "BIGINT" : "INTEGER")})";
        if (fn is "SUM" or "COUNT")
            aggregate = $"COALESCE({aggregate}, 0)";

        var navigation = frame.Navigation;
        var rows = frame.Rows;
        var outer = frame.Outer;
        var key = outer.Table.Columns.FirstOrDefault(c => c.PrimaryKey);
        var target = rows.Table.Columns.FirstOrDefault(c => c.PrimaryKey);
        if (key is null || (navigation.Kind == NavigationKind.Junction && target is null))
            return Fail(call, $"'{outer.Table.Name}' has no key to find its related rows by.");

        string from;
        if (navigation.Kind == NavigationKind.Children)
        {
            from = $"FROM {Q(rows.Table.Name)} {rows.Alias} WHERE {rows.Alias}.{Q(navigation.ForeignKey!)} = {outer.Alias}.{Q(key.Name)}";
        }
        else
        {
            var junction = _shared.NextAlias("j");
            from = $"FROM {Q(rows.Table.Name)} {rows.Alias} JOIN {Q(navigation.Junction!)} {junction} " +
                   $"ON {junction}.{Q(navigation.JunctionTarget!)} = {rows.Alias}.{Q(target!.Name)} " +
                   $"WHERE {junction}.{Q(navigation.JunctionThis!)} = {outer.Alias}.{Q(key.Name)}";
        }
        if (condition is not null)
            from += $" AND {condition.Sql}";

        return new Expr($"(SELECT {aggregate} {from})", type, call);
    }

    /// <summary>
    /// Measures: SUM/COUNT/AVG/MIN/MAX over the rows of the group, with an
    /// optional condition — <c>SUM(amount WHERE amount &lt; 0)</c>, <c>SUMIF(amount, amount &lt; 0)</c>,
    /// <c>COUNT()</c>, <c>COUNT(WHERE paid)</c>, <c>COUNTIF(paid)</c>.
    /// </summary>
    private Expr? BindGroupAggregate(FunctionCall call, string name)
    {
        var conditional = name.EndsWith("IF", StringComparison.Ordinal);
        var fn = conditional ? name[..^2] : name;
        var hasCondition = call.HasWhere || conditional;
        var valueCount = call.Arguments.Count - (hasCondition ? 1 : 0);
        var minValues = fn == "COUNT" ? 0 : 1;
        if (valueCount < minValues || valueCount > 1 || (hasCondition && call.Arguments.Count == 0))
        {
            var info = FormulaFunctions.Find(name)!;
            return Fail(call, fn == "COUNT"
                ? $"Write COUNT(), COUNT(column) or COUNT(WHERE condition) — {info.Description}"
                : $"Write {fn}(column) or {fn}(column WHERE condition), e.g. {fn}(amount WHERE amount < 0).");
        }

        _inGroup = true;
        var value = valueCount == 1 ? Bind(call.Arguments[0]) : null;
        var condition = hasCondition ? Bind(call.Arguments[^1]) : null;
        _inGroup = false;
        if ((valueCount == 1 && value is null) || (hasCondition && condition is null))
            return null;
        if (condition is not null && !RequireBoolean(condition, $"The condition of {name}"))
            return null;
        if (value is { IsRows: true })
            return Fail(value.Node, $"Related rows go into an aggregate per row: {fn}(SUM(…)) adds up a sum per row.");

        string aggregate;
        FormulaType type;
        var guarded = value is null ? "1" : value.Sql;
        if (condition is not null)
            guarded = $"CASE WHEN {condition.Sql} THEN {guarded} END";
        switch (fn)
        {
            case "COUNT":
                aggregate = value is null && condition is null ? "COUNT(*)" : $"COUNT({guarded})";
                type = FormulaType.Integer;
                break;
            case "SUM" or "AVG":
                if (!RequireNumber(value!, name))
                    return null;
                type = fn == "AVG"
                    ? value!.Type == FormulaType.Currency ? FormulaType.Currency : FormulaType.Number
                    : value!.Type == FormulaType.Null ? FormulaType.Number : value.Type;
                aggregate = $"{fn}({guarded})";
                break;
            case "MEDIAN" or "PERCENTILE":
                if (!RequireNumber(value!, name))
                    return null;
                type = value!.Type == FormulaType.Currency ? FormulaType.Currency : FormulaType.Number;
                aggregate = PercentileSql(guarded, fn == "MEDIAN" ? 0.5 : _percentile, _pg);
                break;
            default:
                if (!(FormulaTypes.IsNumeric(value!.Type) || FormulaTypes.IsTemporal(value.Type)))
                    return Fail(value.Node, $"{name} needs numbers or dates, not {FormulaTypes.Describe(value.Type)}.");
                type = value.Type;
                aggregate = $"{fn}({guarded})";
                break;
        }
        if (type == FormulaType.Integer && fn != "COUNT")
            aggregate = $"CAST({aggregate} AS {(_pg ? "BIGINT" : "INTEGER")})";
        if (fn is "SUM" or "COUNT")
            aggregate = $"COALESCE({aggregate}, 0)";
        return new Expr(aggregate, type, call);
    }

    /// <summary>
    /// PERCENTILE(column, p [WHERE condition]): MEDIAN at another point — the
    /// percentile (0–100, a number) is taken out and the rest binds like MEDIAN.
    /// </summary>
    private Expr? BindPercentile(FunctionCall call)
    {
        var usage = "Write PERCENTILE(column, 90) or PERCENTILE(column, 90 WHERE condition), e.g. PERCENTILE(amount, 90).";
        if (call.Arguments.Count < 2 || call.Arguments.Count > (call.HasWhere ? 3 : 2))
            return Fail(call, usage);
        if (call.Arguments[1] is not NumberLiteral { Value: >= 0 and <= 100 } p)
            return Fail(call.Arguments[1], "The percentile is a number from 0 to 100, e.g. PERCENTILE(amount, 90).");
        var rest = call with { Arguments = [call.Arguments[0], .. call.Arguments.Skip(2)] };
        var previous = _percentile;
        _percentile = (double)p.Value / 100;
        try
        {
            return _shared.Context.Measure && !_inGroup ? BindGroupAggregate(rest, "PERCENTILE") : BindAggregate(rest, "PERCENTILE");
        }
        finally
        {
            _percentile = previous;
        }
    }

    /// <summary>
    /// The continuous percentile (0–1) of the non-empty values, interpolated
    /// between neighbours: Postgres' <c>percentile_cont</c>, on SQLite the
    /// registered <c>bento_percentile</c> that computes it the same way.
    /// </summary>
    public static string PercentileSql(string value, double fraction, bool postgres)
    {
        var f = fraction.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        return postgres
            ? $"percentile_cont({f}) WITHIN GROUP (ORDER BY CAST({value} AS DOUBLE PRECISION))"
            : $"bento_percentile({value}, {f})";
    }

    /// <summary>
    /// LOOKUP_ASOF(table, match_column, match_value, date_column, at_date, value_column):
    /// the value of the latest row on or before the date, e.g. a price at a month end.
    /// </summary>
    private Expr? BindLookupAsOf(FunctionCall call)
    {
        if (!Arity(call, 6, 6))
            return null;
        var args = call.Arguments;
        string? Name(int i)
        {
            if (args[i] is StringLiteral literal && !string.IsNullOrWhiteSpace(literal.Value))
                return literal.Value.Trim();
            Fail(args[i], "LOOKUP_ASOF takes table and column names as text, e.g. \"prices\".");
            return null;
        }

        var tableName = Name(0);
        var matchName = Name(1);
        var dateName = Name(3);
        var valueName = Name(5);
        if (tableName is null || matchName is null || dateName is null || valueName is null)
            return null;
        var target = _shared.Schema.Table(tableName);
        if (target is null)
        {
            var suggestion = Suggest(tableName, _shared.Schema.TableNames);
            return Fail(args[0], $"Table '{tableName}' not found" + (suggestion is null ? "." : $" — did you mean '{suggestion}'?"));
        }

        var matchValue = Bind(args[2]);
        var atDate = Bind(args[4]);
        if (matchValue is null || atDate is null)
            return null;

        var alias = _shared.NextAlias("a");
        var outer = _scope;
        var frame = _aggregate;
        var inGroup = _inGroup;
        _scope = new Scope(target, alias);
        _aggregate = null;
        _inGroup = true;
        ColumnReference Ref(int i, string column) => new([column], false, args[i].Start, args[i].End);
        var matchColumn = BindColumn(Ref(1, matchName));
        var dateColumn = BindColumn(Ref(3, dateName));
        var valueColumn = BindColumn(Ref(5, valueName));
        _scope = outer;
        _aggregate = frame;
        _inGroup = inGroup;
        if (matchColumn is null || dateColumn is null || valueColumn is null)
            return null;
        if (!FormulaTypes.IsTemporal(dateColumn.Type))
            return Fail(args[3], $"'{dateName}' is not a date column.");

        var match = Compare(matchColumn, "=", matchValue, args[2]);
        var onOrBefore = Compare(dateColumn, "<=", atDate, args[4]);
        if (match is null || onOrBefore is null)
            return null;

        return new Expr(
            $"(SELECT {valueColumn.Sql} FROM {Q(target.Name)} {alias} WHERE {match.Sql} AND {onOrBefore.Sql} ORDER BY {dateColumn.Sql} DESC LIMIT 1)",
            valueColumn.Type, call);
    }

    /// <summary>A dashboard variable's value as a literal.</summary>
    private Expr Constant(FormulaValue value, FormulaNode node)
    {
        var sql = value.Value switch
        {
            null => "NULL",
            bool b => b ? "TRUE" : "FALSE",
            string text when value.Type == FormulaType.Date && ParseDate(text) is { } date => DateLiteral(date),
            string text when value.Type == FormulaType.DateTime && ParseDateTime(text) is { } stamp => DateTimeLiteral(stamp),
            string text => Str(text),
            decimal d => NumberSql(d, value.Type == FormulaType.Integer),
            long l => NumberSql(l, true),
            double d => NumberSql((decimal)d, false),
            _ => Str(System.Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? "")
        };
        object? constant = value.Value switch { long l => (decimal)l, double d => (decimal)d, _ => value.Value };
        return new Expr(sql, value.Value is null ? FormulaType.Null : value.Type, node, constant);
    }

    // ── Checks ───────────────────────────────────────────────────────────

    private bool Arity(FunctionCall call, int min, int max)
    {
        var count = call.Arguments.Count;
        if (count >= min && count <= max)
            return true;
        var info = FormulaFunctions.Find(call.Name)!;
        Fail(call, $"{info.Name} takes {(min == max ? $"{min}" : max == int.MaxValue ? $"at least {min}" : $"{min} or {max}")} argument{(max == 1 && min == 1 ? "" : "s")}: {info.Signature}.");
        return false;
    }

    private bool RequireBoolean(Expr e, string what)
    {
        if (e.Type is FormulaType.Boolean or FormulaType.Null)
            return true;
        Fail(e.Node, $"{what} needs a condition (yes/no), not {FormulaTypes.Describe(e.Type)}" +
            (e.Type == FormulaType.Text || FormulaTypes.IsNumeric(e.Type) ? " — compare it, e.g. amount < 0." : "."));
        return false;
    }

    private bool RequireNumber(Expr e, string what)
    {
        if (FormulaTypes.IsNumeric(e.Type) || e.Type == FormulaType.Null)
            return true;
        Fail(e.Node, $"{what} needs a number, not {FormulaTypes.Describe(e.Type)}.");
        return false;
    }

    private bool RequireInteger(Expr e, string what)
    {
        if (e.Type is FormulaType.Integer or FormulaType.Null)
            return true;
        Fail(e.Node, FormulaTypes.IsNumeric(e.Type)
            ? $"{what} needs a whole number; wrap it in ROUND(…), FLOOR(…) or CEIL(…)."
            : $"{what} needs a whole number, not {FormulaTypes.Describe(e.Type)}.");
        return false;
    }

    private Expr? RequireTemporal(Expr e, string what)
    {
        if (FormulaTypes.IsTemporal(e.Type))
            return e;
        if (e.Type == FormulaType.Null)
            return e with { Type = FormulaType.Date };
        if (e.Constant is string text)
        {
            if (ParseDate(text) is { } date)
                return new Expr(DateLiteral(date), FormulaType.Date, e.Node);
            if (ParseDateTime(text) is { } stamp)
                return new Expr(DateTimeLiteral(stamp), FormulaType.DateTime, e.Node);
            return Fail(e.Node, $"\"{text}\" is not a date; write it as \"2026-01-31\".");
        }
        return Fail(e.Node, $"{what} needs a date, not {FormulaTypes.Describe(e.Type)}.");
    }

    private string? Unit(Expr e, string what)
    {
        var unit = (e.Constant as string)?.Trim().ToLowerInvariant().TrimEnd('s');
        if (unit is "day" or "week" or "month" or "year")
            return unit + "s";
        Fail(e.Node, $"{what} needs the unit as text: \"days\", \"weeks\", \"months\" or \"years\".");
        return null;
    }

    /// <summary>The common type of IF branches, SWITCH results or COALESCE values.</summary>
    private FormulaType? Unify(IReadOnlyList<Expr> values, FunctionCall call, string what)
    {
        var types = values.Select(v => v.Type).Where(t => t != FormulaType.Null).Distinct().ToList();
        if (types.Count == 0)
            return FormulaType.Null;
        if (types.Count == 1)
            return types[0];
        if (types.All(FormulaTypes.IsNumeric))
            return types.Contains(FormulaType.Currency) ? FormulaType.Currency
                : types.Contains(FormulaType.Number) ? FormulaType.Number : FormulaType.Integer;

        var temporal = types.Where(FormulaTypes.IsTemporal).ToList();
        if (temporal.Count > 0 && values.All(v => FormulaTypes.IsTemporal(v.Type) || v.Type == FormulaType.Null ||
                (v.Constant is string s && (ParseDate(s) is not null || ParseDateTime(s) is not null))))
            return temporal.Contains(FormulaType.DateTime) ||
                   values.Any(v => v.Constant is string s && ParseDate(s) is null) ? FormulaType.DateTime : FormulaType.Date;

        var odd = values.First(v => v.Type != FormulaType.Null && v.Type != types[0]);
        Fail(odd.Node, $"{what} mixes {FormulaTypes.Describe(types[0])} and {FormulaTypes.Describe(odd.Type)}; all results need the same type.");
        return null;
    }

    // ── SQL pieces ───────────────────────────────────────────────────────

    private string RealType => _pg ? "DOUBLE PRECISION" : "REAL";

    /// <summary>A number as floating point, for the finance functions' powers.</summary>
    private string Real(Expr e) => $"CAST({e.Sql} AS {RealType})";

    /// <summary>The value as SQL of the given (compatible) type.</summary>
    private string Convert(Expr e, FormulaType target)
    {
        if (e.Type == target || e.Type == FormulaType.Null)
            return e.Sql;
        if (FormulaTypes.IsTemporal(target))
            return AsTemporal(e, target) ?? e.Sql;
        return e.Sql;
    }

    private string? AsTemporal(Expr e, FormulaType target)
    {
        if (e.Type == target)
            return e.Sql;
        if (e.Type == FormulaType.Date && target == FormulaType.DateTime)
            return _pg ? $"CAST({e.Sql} AS TIMESTAMP)" : $"datetime({e.Sql})";
        if (e.Type == FormulaType.DateTime && target == FormulaType.Date)
            return _pg ? $"CAST({e.Sql} AS DATE)" : $"date({e.Sql})";
        if (e.Constant is string text)
        {
            if (ParseDate(text) is { } date)
                return target == FormulaType.Date ? DateLiteral(date) : DateTimeLiteral(date.ToDateTime(TimeOnly.MinValue));
            if (ParseDateTime(text) is { } stamp)
                return target == FormulaType.DateTime ? DateTimeLiteral(stamp) : null;
        }
        return null;
    }

    /// <summary>Text for &amp;/CONCAT: empty values become "".</summary>
    private string TextOrEmpty(Expr e) =>
        e.Constant is string ? e.Sql : e.Type == FormulaType.Null ? "''" : $"COALESCE({Text(e)}, '')";

    /// <summary>Any value as text, the same on both providers: 2.50 → "2.5", dates as yyyy-MM-dd, TRUE/FALSE.</summary>
    private string Text(Expr e) => e.Type switch
    {
        FormulaType.Text or FormulaType.Null => e.Sql,
        FormulaType.Integer => $"CAST({e.Sql} AS TEXT)",
        FormulaType.Number or FormulaType.Currency => e.Constant is decimal d
            ? Str(Normalize(d).ToString(CultureInfo.InvariantCulture))
            : _pg ? $"CAST(trim_scale(ROUND(CAST({e.Sql} AS NUMERIC), 10)) AS TEXT)" : $"bento_num_text({e.Sql})",
        FormulaType.Boolean => $"(CASE WHEN {e.Sql} THEN 'TRUE' WHEN NOT {e.Sql} THEN 'FALSE' END)",
        FormulaType.Date => _pg ? $"to_char({e.Sql}, 'YYYY-MM-DD')" : e.Sql,
        FormulaType.DateTime => _pg ? $"to_char({e.Sql}, 'YYYY-MM-DD HH24:MI:SS')" : e.Sql,
        _ => e.Sql
    };

    private string LowerFor(Expr e) => e.Constant is string s
        ? Str(s.ToLowerInvariant())
        : _pg ? $"lower({e.Sql})" : $"bento_lower({e.Sql})";

    private string Search(Expr find, Expr within) => _pg
        ? $"strpos({LowerFor(within with { Sql = Text(within) })}, {LowerFor(find with { Sql = Text(find) })})"
        : $"instr({LowerFor(within with { Sql = Text(within) })}, {LowerFor(find with { Sql = Text(find) })})";

    /// <summary>Non-integer numbers rounded to 10 places, so float noise cannot change a result.</summary>
    private string Round10(Expr e)
    {
        if (e.Type is not (FormulaType.Number or FormulaType.Currency) || e.IsConstant)
            return e.Sql;
        return _pg ? $"ROUND(CAST({e.Sql} AS NUMERIC), 10)" : $"ROUND({e.Sql}, 10)";
    }

    private string Divide(string left, string right) => _pg
        ? $"(CAST({left} AS NUMERIC) / NULLIF(CAST({right} AS NUMERIC), 0))"
        : $"(CAST({left} AS REAL) / NULLIF({right}, 0))";

    /// <summary>Empty where Postgres would raise (negative base with a fractional exponent, 0 to a negative power).</summary>
    private string Power(string left, string right) =>
        $"(CASE WHEN ({left} < 0 AND {right} <> floor({right})) OR ({left} = 0 AND {right} < 0) THEN NULL " +
        $"ELSE power(CAST({left} AS {RealType}), CAST({right} AS {RealType})) END)";

    /// <summary>A whole-number argument for Postgres functions that take int4.</summary>
    private string Int(Expr e) => _pg && !e.IsConstant ? $"CAST({e.Sql} AS INTEGER)" : e.Sql;

    private string Part(Expr date, string part)
    {
        if (_pg)
        {
            var source = date.Type == FormulaType.Date && part is "HOUR" or "MINUTE" ? $"CAST({date.Sql} AS TIMESTAMP)" : date.Sql;
            return $"CAST(EXTRACT({part} FROM {source}) AS BIGINT)";
        }
        var format = part switch { "YEAR" => "%Y", "MONTH" => "%m", "DAY" => "%d", "HOUR" => "%H", _ => "%M" };
        return $"CAST(strftime('{format}', {date.Sql}) AS INTEGER)";
    }

    /// <summary>Whole units from start to end; months count complete months (Jan 31 → Feb 28 is 0).</summary>
    private string DateDiff(Expr start, Expr end, string unit)
    {
        if (unit is "days" or "weeks")
        {
            var days = _pg
                ? $"(CAST({end.Sql} AS DATE) - CAST({start.Sql} AS DATE))"
                : $"CAST(julianday(date({end.Sql})) - julianday(date({start.Sql})) AS INTEGER)";
            return unit == "days" ? days : $"({days} / 7)";
        }

        string P(Expr e, string part) => Part(e, part);
        var months = $"(({P(end, "YEAR")} - {P(start, "YEAR")}) * 12 + ({P(end, "MONTH")} - {P(start, "MONTH")}))";
        var (endDay, startDay) = (P(end, "DAY"), P(start, "DAY"));
        var whole = $"(CASE WHEN {months} > 0 AND {endDay} < {startDay} THEN {months} - 1 " +
                    $"WHEN {months} < 0 AND {endDay} > {startDay} THEN {months} + 1 ELSE {months} END)";
        return unit == "months" ? whole : $"({whole} / 12)";
    }

    /// <summary>Adds whole units; month ends are clamped (Jan 31 + 1 month = Feb 28) on both providers.</summary>
    private string DateAdd(Expr date, string count, string unit)
    {
        if (_pg)
        {
            var n = $"CAST({count} AS INTEGER)";
            if (date.Type == FormulaType.Date && unit is "days" or "weeks")
                return $"({date.Sql} + {n}{(unit == "weeks" ? " * 7" : "")})";
            var interval = $"make_interval({unit} => {n})";
            return date.Type == FormulaType.Date
                ? $"CAST({date.Sql} + {interval} AS DATE)"
                : $"({date.Sql} + {interval})";
        }

        var fn = date.Type == FormulaType.Date ? "date" : "datetime";
        return unit switch
        {
            "days" => $"{fn}({date.Sql}, ({count}) || ' days')",
            "weeks" => $"{fn}({date.Sql}, (({count}) * 7) || ' days')",
            _ => $"{fn}({date.Sql}, ({count}) || ' {unit}', 'floor')"
        };
    }

    // ── Literals ─────────────────────────────────────────────────────────

    private static string NumberSql(decimal value, bool isInteger)
    {
        var text = isInteger
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
        // Parenthesized: "a - -5" would start an SQL comment.
        return value < 0 ? $"({text})" : text;
    }

    /// <summary>
    /// A text literal. A "?" is written as char(63): SqlKata reads "?" in raw
    /// SQL as a parameter placeholder, even inside quotes.
    /// </summary>
    private string Str(string value)
    {
        var parts = value.Split('?').Select(part => "'" + part.Replace("'", "''") + "'");
        return value.Contains('?')
            ? "(" + string.Join($" || {(_pg ? "chr" : "char")}(63) || ", parts) + ")"
            : parts.Single();
    }

    private string DateLiteral(DateOnly date)
    {
        var text = Str(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return _pg ? $"DATE {text}" : text;
    }

    private string DateTimeLiteral(DateTime stamp)
    {
        var text = Str(stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        return _pg ? $"TIMESTAMP {text}" : text;
    }

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static DateTime? ParseDateTime(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 16 || !char.IsDigit(trimmed[0]))
            return null;
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)
            ? stamp.UtcDateTime
            : null;
    }

    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    // ── Errors ───────────────────────────────────────────────────────────

    private Expr? Fail(FormulaNode node, string message) => Fail(node.Start, node.End, message);

    private Expr? Fail(int start, int end, string message)
    {
        _errors.Add(new FormulaError(message, start, end));
        return null;
    }

    /// <summary>The closest candidate within a small edit distance ("did you mean").</summary>
    internal static string? Suggest(string input, IEnumerable<string> candidates)
    {
        var needle = input.ToLowerInvariant();
        var best = candidates
            .Select(c => (Candidate: c, Distance: Distance(needle, c.ToLowerInvariant())))
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        return best.Candidate is not null && best.Distance <= Math.Max(1, needle.Length / 3) ? best.Candidate : null;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            previous = current;
        }
        return previous[b.Length];
    }
}
