using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Formula;

namespace Bento.Tests;

/// <summary>
/// Steps 3.4 and 3.5: parser, type checker, links, aggregates and messages,
/// without a database. The values are covered by <see cref="FormulaConformanceTests"/>
/// and <see cref="ComputedColumnTests"/> on both providers.
/// </summary>
public sealed class FormulaEngineTests
{
    private static readonly TableDef Transactions = new()
    {
        Name = "transactions",
        DisplayName = "Transactions",
        Columns =
        [
            new() { Name = "id", Type = ColumnType.Integer, PrimaryKey = true },
            new() { Name = "booked_on", Type = ColumnType.Date, DisplayName = "Booked on" },
            new() { Name = "recorded_at", Type = ColumnType.DateTime },
            new() { Name = "created_at", Type = ColumnType.DateTime },
            new() { Name = "amount", Type = ColumnType.Currency },
            new() { Name = "quantity", Type = ColumnType.Integer },
            new() { Name = "area", Type = ColumnType.Float },
            new() { Name = "counterparty", Type = ColumnType.Text },
            new() { Name = "note", Type = ColumnType.Text },
            new() { Name = "purpose", Type = ColumnType.Text },
            new() { Name = "iban", Type = ColumnType.Text },
            new() { Name = "reference", Type = ColumnType.Text },
            new() { Name = "email", Type = ColumnType.Text },
            new() { Name = "ticker", Type = ColumnType.Text },
            new() { Name = "paid", Type = ColumnType.Boolean },
            new() { Name = "status", Type = ColumnType.Enum, Values = ["open", "done"] },
            new() { Name = "meta", Type = ColumnType.Json },
            new() { Name = "net", Type = ColumnType.Computed, Formula = "amount" },
            new() { Name = "category_id", Type = ColumnType.Reference, References = "categories" },
            new() { Name = "account_id", Type = ColumnType.Reference, References = "accounts" }
        ]
    };

    private static readonly TableDef Categories = new()
    {
        Name = "categories",
        Columns =
        [
            new() { Name = "id", Type = ColumnType.Integer, PrimaryKey = true },
            new() { Name = "name", Type = ColumnType.Text }
        ]
    };

    private static readonly TableDef Accounts = new()
    {
        Name = "accounts",
        Columns =
        [
            new() { Name = "id", Type = ColumnType.Integer, PrimaryKey = true },
            new() { Name = "name", Type = ColumnType.Text },
            new() { Name = "opening_balance", Type = ColumnType.Currency },
            new() { Name = "balance", Type = ColumnType.Computed, Formula = "opening_balance + SUM(transactions.amount)" },
            new() { Name = "a", Type = ColumnType.Computed, Formula = "b + 1" },
            new() { Name = "b", Type = ColumnType.Computed, Formula = "a + 1" },
            new() { Name = "selfish", Type = ColumnType.Computed, Formula = "selfish + 1" },
            new() { Name = "broken", Type = ColumnType.Computed, Formula = "nothing * 2" }
        ]
    };

    private static readonly TableDef Prices = new()
    {
        Name = "prices",
        Columns =
        [
            new() { Name = "id", Type = ColumnType.Integer, PrimaryKey = true },
            new() { Name = "ticker", Type = ColumnType.Text },
            new() { Name = "date", Type = ColumnType.Date },
            new() { Name = "price", Type = ColumnType.Currency }
        ]
    };

    private static readonly FormulaSchema Schema = new(
        [Transactions, Categories, Accounts, Prices],
        [
            new RelationDef { Name = "category_transactions", Type = RelationType.OneToMany, From = "categories", To = "transactions", Column = "category_id" },
            new RelationDef { Name = "account_transactions", Type = RelationType.OneToMany, From = "accounts", To = "transactions", Column = "account_id" }
        ]);

    private static FormulaCheck Check(string formula, DatabaseProvider provider = DatabaseProvider.Sqlite, FormulaType? expected = null, TableDef? table = null, string? column = null) =>
        FormulaCompiler.Compile(formula, table ?? Transactions, Schema, provider, expected: expected, column: column);

    private static FormulaError SingleError(string formula)
    {
        var check = Check(formula);
        Assert.False(check.IsValid);
        return Assert.Single(check.Errors);
    }

    [Theory]
    [InlineData("= amount * -1", "currency")]
    [InlineData("amount * -1", "currency")]
    [InlineData("= IF(amount < 0, \"expense\", \"income\")", "text")]
    [InlineData("= YEAR(booked_on)", "integer")]
    [InlineData("= [Booked on] > \"2026-01-01\"", "boolean")]
    [InlineData("= if(amount <> 0; 1; 2)", "integer")]
    [InlineData("= amount / quantity", "currency")]
    [InlineData("= amount / amount", "float")]
    [InlineData("= quantity / 2", "float")]
    [InlineData("= quantity * area", "float")]
    [InlineData("= booked_on + 7", "date")]
    [InlineData("= recorded_at - booked_on", "integer")]
    [InlineData("= IF(paid, recorded_at, booked_on)", "datetime")]
    [InlineData("= COALESCE(quantity, area, amount)", "currency")]
    [InlineData("= NOT paid AND !(quantity > 1) || status = \"open\"", "boolean")]
    [InlineData("= NULL", "text")]
    [InlineData("= category_id + 1", "integer")]
    [InlineData("= category_id.name", "text")]
    [InlineData("= UPPER(category.name)", "text")]
    [InlineData("= account.balance - amount", "currency")]
    [InlineData("= net * 2", "currency")]
    [InlineData("= \"what?\" & note", "text")]
    public void ValidFormulas_HaveTheirType(string formula, string type)
    {
        var check = Check(formula);
        Assert.True(check.IsValid, string.Join("; ", check.Errors.Select(e => e.Message)));
        Assert.Equal(type, FormulaTypes.Name(check.Result!.Type));
    }

    [Fact]
    public void ReferencedColumns_AreListedOnce()
    {
        var check = Check("= IF(amount < 0, amount * -1, quantity + [Booked on] - booked_on)");
        Assert.Equal(["amount", "quantity", "booked_on"], check.Result!.Columns);
    }

    [Theory]
    [InlineData("= amount *", "The formula ends too early; something is missing here.", 10, 10)]
    [InlineData("= (amount + 1", "This ( is never closed.", 2, 3)]
    [InlineData("= amount + 1)", "This ) has no matching (.", 12, 13)]
    [InlineData("= \"open", "This text is missing its closing quote.", 2, 7)]
    [InlineData("= amount amount", "Expected an operator before 'amount'.", 9, 15)]
    [InlineData("= IF(amount < 0,, 1)", "An argument is missing here.", 16, 17)]
    [InlineData("= ROUND(amount, 2", "ROUND( is never closed.", 2, 8)]
    [InlineData("= 1 < 2 < 3", "Comparisons cannot be chained; combine them with AND.", 8, 9)]
    [InlineData("= amount # 2", "'#' is not allowed here.", 9, 10)]
    [InlineData("=", "Write a formula, for example amount * -1.", 0, 1)]
    [InlineData("= 12abc", "'12a' is not a number.", 2, 5)]
    public void SyntaxErrors_PointAtTheProblem(string formula, string message, int start, int end)
    {
        var error = SingleError(formula);
        Assert.Equal(message, error.Message);
        Assert.Equal((start, end), (error.Start, error.End));
    }

    [Theory]
    [InlineData("= amout * 2", "Column 'amout' not found in Transactions — did you mean 'amount'?", 2, 7)]
    [InlineData("= SUMM(amount)", "Unknown function SUMM — did you mean SUM?", 2, 6)]
    [InlineData("= SUM(amount)", "SUM adds up related rows; name the link, e.g. SUM(transactions.amount).", 2, 13)]
    [InlineData("= CONCATE(note, \"x\")", "Unknown function CONCATE — did you mean CONCAT?", 2, 9)]
    [InlineData("= today", "TODAY is a function; call it with parentheses: TODAY(…).", 2, 7)]
    [InlineData("= orders.total", "'orders' is not a column or link of Transactions.", 2, 14)]
    [InlineData("= categry.name", "'categry' is not a column or link of Transactions — did you mean 'category'?", 2, 14)]
    [InlineData("= amount.total", "'amount' is not a link; only reference columns and relations can be followed with a dot.", 2, 14)]
    [InlineData("= category.nme", "Column 'nme' not found in categories — did you mean 'name'?", 2, 14)]
    [InlineData("= meta", "'meta' is a json column; formulas cannot use those.", 2, 6)]
    public void UnknownNames_SuggestTheClosestMatch(string formula, string message, int start, int end)
    {
        var error = SingleError(formula);
        Assert.Equal(message, error.Message);
        Assert.Equal((start, end), (error.Start, error.End));
    }

    [Theory]
    [InlineData("= amount + note", "+ needs a number, not text.")]
    [InlineData("= IF(amount, 1, 2)", "IF needs a condition (yes/no), not an amount — compare it, e.g. amount < 0.")]
    [InlineData("= IF(paid, 1, \"one\")", "IF mixes a whole number and text; all results need the same type.")]
    [InlineData("= amount < \"10\"", "Cannot compare an amount with text.")]
    [InlineData("= booked_on > note", "Cannot compare a date with text.")]
    [InlineData("= booked_on > \"next week\"", "Cannot compare a date with text.")]
    [InlineData("= paid < TRUE", "Yes/no values can only be compared with = or <>.")]
    [InlineData("= MOD(amount, 2)", "MOD needs a whole number; wrap it in ROUND(…), FLOOR(…) or CEIL(…).")]
    [InlineData("= LEFT(note)", "LEFT takes 2 arguments: LEFT(text, count).")]
    [InlineData("= IF(paid)", "IF takes 2 or 3 arguments: IF(condition, then, [else]).")]
    [InlineData("= TODAY(1)", "TODAY takes 0 arguments: TODAY().")]
    [InlineData("= ROUND(amount, 11)", "ROUND takes the number of decimal places as a whole number from 0 to 10.")]
    [InlineData("= DATEADD(booked_on, 1, \"fortnights\")", "DATEADD needs the unit as text: \"days\", \"weeks\", \"months\" or \"years\".")]
    [InlineData("= DATEADD(booked_on, 1.5, \"days\")", "DATEADD needs a whole number; wrap it in ROUND(…), FLOOR(…) or CEIL(…).")]
    [InlineData("= YEAR(\"31.01.2026\")", "\"31.01.2026\" is not a date; write it as \"2026-01-31\".")]
    [InlineData("= booked_on + booked_on", "Dates can only be added to a whole number of days; use DATEADD for months or years.")]
    [InlineData("= -note", "Only numbers can be negated, not text.")]
    [InlineData("= amount < NULL", "Empty values cannot be ordered; test them with ISBLANK(…).")]
    public void TypeErrors_ExplainWhatFits(string formula, string message)
    {
        Assert.Equal(message, SingleError(formula).Message);
    }

    [Theory]
    [InlineData("= SUM(transactions.amount)", "currency")]
    [InlineData("= opening_balance + SUM(transactions.amount)", "currency")]
    [InlineData("= SUM(transactions.amount * -1, transactions.amount < 0)", "currency")]
    [InlineData("= SUMIF(transactions.amount, transactions.paid)", "currency")]
    [InlineData("= COUNT(transactions)", "integer")]
    [InlineData("= COUNTIF(transactions, transactions.amount < opening_balance)", "integer")]
    [InlineData("= COUNT(transactions.note)", "integer")]
    [InlineData("= SUM(transactions.quantity)", "integer")]
    [InlineData("= AVG(transactions.quantity)", "float")]
    [InlineData("= AVG(transactions.amount)", "currency")]
    [InlineData("= MAX(transactions.booked_on)", "date")]
    [InlineData("= MIN(transactions.recorded_at)", "datetime")]
    [InlineData("= SUM(transactions.net)", "currency")]
    [InlineData("= COUNT(transactions, transactions.category.name = \"Food\")", "integer")]
    [InlineData("= balance * 2", "currency")]
    public void Aggregates_OverRelatedRows(string formula, string type)
    {
        var check = Check(formula, table: Accounts);
        Assert.True(check.IsValid, string.Join("; ", check.Errors.Select(e => e.Message)));
        Assert.Equal(type, FormulaTypes.Name(check.Result!.Type));
    }

    [Theory]
    [InlineData("= transactions.amount", "'transactions' holds several rows; use SUM(transactions.amount), COUNT(transactions), AVG, MIN or MAX.")]
    [InlineData("= transactions", "'transactions' holds several rows; use SUM(transactions.column), COUNT(transactions), AVG, MIN or MAX.")]
    [InlineData("= SUM(transactions.note)", "SUM needs a number, not text.")]
    [InlineData("= SUM(transactions)", "SUM needs a column of the related rows, e.g. SUM(transactions.amount).")]
    [InlineData("= SUM(transactions.amount, transactions.note)", "The condition of SUM needs a condition (yes/no), not text — compare it, e.g. amount < 0.")]
    [InlineData("= SUM(SUM(transactions.amount))", "SUM cannot be used inside another aggregate.")]
    [InlineData("= SUMIF(transactions.amount)", "SUMIF takes 2 arguments: SUMIF(link.column, condition).")]
    [InlineData("= MAX(transactions.note)", "MAX needs numbers or dates, not text.")]
    [InlineData("= a", "Circular reference: accounts.a → accounts.b → accounts.a.")]
    [InlineData("= selfish", "Circular reference: accounts.selfish → accounts.selfish.")]
    [InlineData("= broken + 1", "'broken' has an error: Column 'nothing' not found in accounts.")]
    public void AggregateAndDependencyErrors(string formula, string message)
    {
        var check = Check(formula, table: Accounts);
        Assert.False(check.IsValid);
        Assert.Equal(message, check.Errors[0].Message);
    }

    private static MeasureCheck Measure(string formula, TableDef? table = null, FormulaContext? context = null) =>
        FormulaCompiler.CompileMeasure(formula, table ?? Transactions, Schema, DatabaseProvider.Sqlite, context: context);

    [Theory]
    [InlineData("SUM(amount WHERE amount > 0)", "currency", false)]
    [InlineData("SUMIF(amount, amount < 0)", "currency", false)]
    [InlineData("COUNT()", "integer", false)]
    [InlineData("COUNT(WHERE paid)", "integer", false)]
    [InlineData("COUNTIF(paid)", "integer", false)]
    [InlineData("COUNT(note WHERE paid)", "integer", false)]
    [InlineData("(SUM(amount WHERE amount > 0) + SUM(amount WHERE amount < 0)) / SUM(amount WHERE amount > 0)", "float", false)]
    [InlineData("ROUND(AVG(amount), 2)", "currency", false)]
    [InlineData("MAX(booked_on)", "date", false)]
    [InlineData("SUM(net) * 2", "currency", false)]
    [InlineData("SUM(account.opening_balance)", "currency", false)]
    [InlineData("RUNNING_SUM(SUM(amount))", "currency", true)]
    [InlineData("SUM(amount) / months", "currency", false)]
    public void Measures_AggregateTheGroup(string formula, string type, bool running)
    {
        var (check, isRunning) = Measure(formula, context: new FormulaContext
        {
            Variables = new Dictionary<string, FormulaValue> { ["months"] = new(FormulaType.Integer, 3L) }
        });
        Assert.True(check.IsValid, string.Join("; ", check.Errors.Select(e => e.Message)));
        Assert.Equal(type, FormulaTypes.Name(check.Result!.Type));
        Assert.Equal(running, isRunning);
    }

    [Fact]
    public void Measures_CanNestRelatedRowAggregates()
    {
        var (check, _) = Measure("SUM(opening_balance + SUM(transactions.amount WHERE transactions.amount < 0))", Accounts);
        Assert.True(check.IsValid, string.Join("; ", check.Errors.Select(e => e.Message)));
        Assert.Equal("currency", FormulaTypes.Name(check.Result!.Type));
    }

    [Theory]
    [InlineData("amount * 2", "'amount' differs from row to row; use it inside SUM(…), AVG(…), MIN(…), MAX(…) or COUNT(…).")]
    [InlineData("SUM(amount) + quantity", "'quantity' differs from row to row; use it inside SUM(…), AVG(…), MIN(…), MAX(…) or COUNT(…).")]
    [InlineData("SUM()", "Write SUM(column) or SUM(column WHERE condition), e.g. SUM(amount WHERE amount < 0).")]
    [InlineData("SUM(amount WHERE note)", "The condition of SUM needs a condition (yes/no), not text — compare it, e.g. amount < 0.")]
    [InlineData("UPPER(note WHERE paid)", "WHERE only works inside SUM, COUNT, AVG, MIN or MAX, not in UPPER.")]
    [InlineData("SUM(amount) + RUNNING_SUM(SUM(amount))", "RUNNING_SUM wraps a whole measure: RUNNING_SUM(SUM(amount)).")]
    [InlineData("RUNNING_SUM(MAX(booked_on))", "RUNNING_SUM needs a number, not a date.")]
    [InlineData("SUM(amount WHERE booked_on <= BUCKET_END())", "BUCKET_END() only works in queries by period end (\"Value at the end of each period\").")]
    [InlineData("SUM(amount WHERE)", "Write a condition after WHERE.")]
    public void MeasureErrors(string formula, string message)
    {
        var (check, _) = Measure(formula);
        Assert.False(check.IsValid);
        Assert.Equal(message, check.Errors[0].Message);
    }

    [Fact]
    public void Measures_ExplainHowToSumRelatedRows()
    {
        var (check, _) = Measure("SUM(transactions.amount)", Accounts);
        Assert.Equal("In a measure, SUM(…) adds up the rows of the group; for the related rows of each row nest it: SUM(SUM(transactions.amount)).", check.Errors[0].Message);
    }

    [Theory]
    [InlineData("= LOOKUP_ASOF(\"prices\", \"ticker\", note, \"date\", booked_on, \"price\")", null)]
    [InlineData("= LOOKUP_ASOF(\"pricez\", \"ticker\", note, \"date\", booked_on, \"price\")", "Table 'pricez' not found — did you mean 'prices'?")]
    [InlineData("= LOOKUP_ASOF(\"prices\", \"ticker\", note, \"ticker\", booked_on, \"price\")", "'ticker' is not a date column.")]
    [InlineData("= LOOKUP_ASOF(\"prices\", ticker, note, \"date\", booked_on, \"price\")", "LOOKUP_ASOF takes table and column names as text, e.g. \"prices\".")]
    [InlineData("= LOOKUP_ASOF(\"prices\", \"ticker\", note, \"date\", booked_on, \"prise\")", "Column 'prise' not found in prices — did you mean 'price'?")]
    public void LookupAsOf(string formula, string? error)
    {
        var check = Check(formula);
        if (error is null)
        {
            Assert.True(check.IsValid, string.Join("; ", check.Errors.Select(e => e.Message)));
            Assert.Equal("currency", FormulaTypes.Name(check.Result!.Type));
        }
        else
        {
            Assert.Equal(error, check.Errors[0].Message);
        }
    }

    [Fact]
    public void AColumnUsingItself_IsACycle()
    {
        var check = Check("= opening_balance + balance", table: Accounts, column: "balance");
        Assert.Equal("Circular reference: accounts.balance → accounts.balance.", Assert.Single(check.Errors).Message);
    }

    [Theory]
    [InlineData(DatabaseProvider.Sqlite, "char(63)")]
    [InlineData(DatabaseProvider.Postgres, "chr(63)")]
    public void QuestionMarks_AreNotWrittenIntoTheSql(DatabaseProvider provider, string replacement)
    {
        var sql = Check("= \"what?\" & note", provider).Result!.Sql;
        Assert.DoesNotContain("?", sql);
        Assert.Contains(replacement, sql);
    }

    [Fact]
    public void SeveralTypeErrors_AreAllReported()
    {
        var check = Check("= amount + note & YEAR(note)");
        Assert.Equal(["+ needs a number, not text.", "YEAR needs a date, not text."], check.Errors.Select(e => e.Message));
    }

    [Theory]
    [InlineData("amount * 2", "currency", true)]
    [InlineData("quantity * 2", "currency", true)]
    [InlineData("amount * 2", "integer", false)]
    [InlineData("booked_on", "datetime", true)]
    [InlineData("recorded_at", "date", false)]
    [InlineData("note", "integer", false)]
    public void DeclaredResultType_MustFit(string formula, string resultType, bool fits)
    {
        var check = Check(formula, expected: FormulaTypes.FromName(resultType));
        Assert.Equal(fits, check.IsValid);
        if (fits)
            Assert.Equal(resultType, FormulaTypes.Name(check.Result!.Type));
        else
            Assert.StartsWith("The formula gives", Assert.Single(check.Errors).Message);
    }

    [Theory]
    [InlineData(DatabaseProvider.Sqlite)]
    [InlineData(DatabaseProvider.Postgres)]
    public void EveryCatalogExample_Compiles(DatabaseProvider provider)
    {
        foreach (var function in FormulaFunctions.Catalog)
        {
            // Aggregates run over an account's transactions; measure functions in a query by period end.
            var check = function.Category == FormulaFunctions.Measures
                ? FormulaCompiler.CompileMeasure(function.Example, Transactions, Schema, provider, context: new FormulaContext { BucketAlias = "b" }).Check
                : Check(function.Example, provider, table: function.Category == FormulaFunctions.RelatedRows ? Accounts : null);
            Assert.True(check.IsValid, $"{function.Name}: {function.Example} → {string.Join("; ", check.Errors.Select(e => e.Message))}");
            Assert.Contains(function.Name, function.Example, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(DatabaseProvider.Sqlite)]
    [InlineData(DatabaseProvider.Postgres)]
    public void Literals_AreQuotedAndNegativesParenthesized(DatabaseProvider provider)
    {
        var sql = Check("= note & \"'); DROP TABLE x; --\" & (quantity - -5)", provider).Result!.Sql;
        Assert.Contains("''); DROP TABLE x; --'", sql);
        Assert.Contains("- (-5)", sql);
        Assert.DoesNotContain("--5", sql);
    }

    [Fact]
    public void NumberText_MatchesPostgresTrimScale()
    {
        Assert.Equal("2", SqliteFormulaFunctions.NumberText(2.0));
        Assert.Equal("0.3", SqliteFormulaFunctions.NumberText(0.1 + 0.2));
        Assert.Equal("-45.9", SqliteFormulaFunctions.NumberText(-45.9));
        Assert.Equal("100000000000000000000", SqliteFormulaFunctions.NumberText(1e20));
        Assert.Equal("0.0000000001", SqliteFormulaFunctions.NumberText(1e-10));
        Assert.Equal("0", SqliteFormulaFunctions.NumberText(1e-12));
        Assert.Null(SqliteFormulaFunctions.NumberText(null));
    }
}
