using System.Globalization;
using Bento.Api.Db;
using Bento.Api.Db.Schema;

namespace Bento.Api.Formula;

/// <summary>A formula translated into a SQL expression over one row of its table.</summary>
public sealed record CompiledFormula(string Sql, FormulaType Type, IReadOnlyList<string> Columns);

public sealed record FormulaCheck(CompiledFormula? Result, IReadOnlyList<FormulaError> Errors)
{
    public bool IsValid => Result is not null && Errors.Count == 0;
}

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
    private sealed class Shared(FormulaSchema schema, bool pg)
    {
        public FormulaSchema Schema { get; } = schema;
        public bool Pg { get; } = pg;
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
        string? column = null)
    {
        var shared = new Shared(schema, provider == DatabaseProvider.Postgres);
        if (column is not null)
            shared.Chain.Add($"{table.Name}.{column}");
        return new FormulaCompiler(shared, new Scope(table, alias)).Run(formula, expected);
    }

    private FormulaCheck Run(string? formula, FormulaType? expected)
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
                    $"The formula gives {FormulaTypes.Describe(type)}, but the column holds {FormulaTypes.Describe(target)}.", 0, (formula ?? "").Length)]);
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
        var check = new FormulaCompiler(_shared, _scope).Run(column.Formula, FormulaTypes.FromName(column.ResultType));
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
        if (info.Category == FormulaFunctions.RelatedRows)
            return BindAggregate(call, name);

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
