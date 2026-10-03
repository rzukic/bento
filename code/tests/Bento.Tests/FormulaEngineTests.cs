using Bento.Api.Db;
using Bento.Api.Db.Schema;
using Bento.Api.Formula;

namespace Bento.Tests;

/// <summary>
/// Step 3.4: parser, type checker and messages, without a database. The
/// values are covered by <see cref="FormulaConformanceTests"/> on both providers.
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
            new() { Name = "paid", Type = ColumnType.Boolean },
            new() { Name = "status", Type = ColumnType.Enum, Values = ["open", "done"] },
            new() { Name = "meta", Type = ColumnType.Json },
            new() { Name = "net", Type = ColumnType.Computed, Formula = "amount" },
            new() { Name = "category_id", Type = ColumnType.Reference, References = "categories" }
        ]
    };

    private static FormulaCheck Check(string formula, DatabaseProvider provider = DatabaseProvider.Sqlite, FormulaType? expected = null) =>
        FormulaCompiler.Compile(formula, Transactions, _ => null, provider, expected: expected);

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
    [InlineData("= SUMM(amount)", "Unknown function SUMM.", 2, 6)]
    [InlineData("= SUM(amount)", "SUM works across several rows; formulas on a single row cannot use it yet.", 2, 5)]
    [InlineData("= CONCATE(note, \"x\")", "Unknown function CONCATE — did you mean CONCAT?", 2, 9)]
    [InlineData("= today", "TODAY is a function; call it with parentheses: TODAY(…).", 2, 7)]
    [InlineData("= orders.total", "Column 'orders' not found in Transactions.", 2, 14)]
    [InlineData("= category_id.name", "Values from related tables (category_id.name) are not supported in formulas yet.", 2, 18)]
    [InlineData("= net * 2", "'net' is computed itself; formulas cannot use computed columns yet.", 2, 5)]
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
            var check = Check(function.Example, provider);
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
