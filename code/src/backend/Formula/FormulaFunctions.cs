using Bento.Api.Db;

namespace Bento.Api.Formula;

/// <summary>The value types a formula works with.</summary>
public enum FormulaType
{
    /// <summary>The literal NULL: fits any type.</summary>
    Null,
    Text,
    Integer,
    /// <summary>A floating-point number (column type <c>float</c>).</summary>
    Number,
    Currency,
    Boolean,
    Date,
    DateTime
}

public static class FormulaTypes
{
    public static bool IsNumeric(FormulaType type) => type is FormulaType.Integer or FormulaType.Number or FormulaType.Currency;

    public static bool IsTemporal(FormulaType type) => type is FormulaType.Date or FormulaType.DateTime;

    /// <summary>The name used in the Model JSON and the API (<c>float</c> for numbers).</summary>
    public static string Name(FormulaType type) => type switch
    {
        FormulaType.Null => "text",
        FormulaType.Number => "float",
        FormulaType.DateTime => "datetime",
        _ => type.ToString().ToLowerInvariant()
    };

    /// <summary>Words for error messages: "a number", "text", "a date".</summary>
    public static string Describe(FormulaType type) => type switch
    {
        FormulaType.Null => "empty",
        FormulaType.Text => "text",
        FormulaType.Integer => "a whole number",
        FormulaType.Number => "a number",
        FormulaType.Currency => "an amount",
        FormulaType.Boolean => "yes/no",
        FormulaType.Date => "a date",
        FormulaType.DateTime => "a date and time",
        _ => type.ToString()
    };

    public static FormulaType? FromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "text" => FormulaType.Text,
        "integer" => FormulaType.Integer,
        "float" or "number" => FormulaType.Number,
        "currency" => FormulaType.Currency,
        "boolean" => FormulaType.Boolean,
        "date" => FormulaType.Date,
        "datetime" => FormulaType.DateTime,
        _ => null
    };

    /// <summary>The column type a formula's value behaves like.</summary>
    public static ColumnType ToColumnType(FormulaType type) => type switch
    {
        FormulaType.Integer => ColumnType.Integer,
        FormulaType.Number => ColumnType.Float,
        FormulaType.Currency => ColumnType.Currency,
        FormulaType.Boolean => ColumnType.Boolean,
        FormulaType.Date => ColumnType.Date,
        FormulaType.DateTime => ColumnType.DateTime,
        _ => ColumnType.Text
    };

    public static FormulaType? FromColumn(ColumnType type) => type switch
    {
        ColumnType.Text or ColumnType.Enum or ColumnType.File or ColumnType.Uuid => FormulaType.Text,
        ColumnType.Integer or ColumnType.Reference => FormulaType.Integer,
        ColumnType.Float => FormulaType.Number,
        ColumnType.Currency => FormulaType.Currency,
        ColumnType.Boolean => FormulaType.Boolean,
        ColumnType.Date => FormulaType.Date,
        ColumnType.DateTime => FormulaType.DateTime,
        _ => null
    };
}

public sealed record FormulaFunctionInfo(string Name, string Category, string Signature, string Description, string Example, string Returns);

/// <summary>
/// The functions formulas can call, for autocomplete and the docs. The
/// compiler implements exactly these (a test keeps both in step).
/// </summary>
public static class FormulaFunctions
{

    public static readonly IReadOnlyList<FormulaFunctionInfo> Catalog =
    [
        new("IF", "Logic", "IF(condition, then, [else])", "Returns then when the condition is true, otherwise else (empty when left out).", "IF(amount < 0, \"expense\", \"income\")", "the type of then/else"),
        new("AND", "Logic", "AND(condition1, condition2, …)", "True when every condition is true. Also written a AND b or a && b.", "AND(amount < 0, YEAR(booked_on) = 2026)", "boolean"),
        new("OR", "Logic", "OR(condition1, condition2, …)", "True when at least one condition is true. Also written a OR b or a || b.", "OR(counterparty = \"REWE\", counterparty = \"Aldi\")", "boolean"),
        new("NOT", "Logic", "NOT(condition)", "Turns true into false and false into true.", "NOT(ISBLANK(note))", "boolean"),
        new("SWITCH", "Logic", "SWITCH(value, match1, result1, …, [default])", "Compares value with each match and returns the result of the first that is equal.", "SWITCH(MONTH(booked_on), 12, \"December\", 1, \"January\", \"other\")", "the type of the results"),
        new("COALESCE", "Logic", "COALESCE(value1, value2, …)", "The first value that is not empty.", "COALESCE(note, counterparty, \"unknown\")", "the type of the values"),
        new("ISBLANK", "Logic", "ISBLANK(value)", "True when the value is empty (text counts as empty when it is \"\").", "ISBLANK(category_id)", "boolean"),

        new("ROUND", "Math", "ROUND(number, [digits])", "Rounds half away from zero to 0–10 decimal places (default 0).", "ROUND(amount * 1.19, 2)", "the type of number"),
        new("FLOOR", "Math", "FLOOR(number)", "Rounds down to a whole number.", "FLOOR(amount / 100)", "integer"),
        new("CEIL", "Math", "CEIL(number)", "Rounds up to a whole number. Also CEILING.", "CEIL(quantity / 6)", "integer"),
        new("ABS", "Math", "ABS(number)", "The number without its sign.", "ABS(amount)", "the type of number"),
        new("MOD", "Math", "MOD(number, divisor)", "The remainder of a whole-number division; it keeps the sign of number. Empty when divisor is 0.", "MOD(quantity, 2)", "integer"),
        new("POWER", "Math", "POWER(number, exponent)", "number raised to exponent. Also written number ^ exponent.", "POWER(1.05, 10)", "float"),
        new("SQRT", "Math", "SQRT(number)", "The square root; empty for negative numbers.", "SQRT(area)", "float"),

        new("CONCAT", "Text", "CONCAT(value1, value2, …)", "Joins values as text; empty values count as \"\". Also written a & b.", "CONCAT(counterparty, \" (\", YEAR(booked_on), \")\")", "text"),
        new("LEFT", "Text", "LEFT(text, count)", "The first count characters.", "LEFT(iban, 2)", "text"),
        new("RIGHT", "Text", "RIGHT(text, count)", "The last count characters.", "RIGHT(iban, 4)", "text"),
        new("MID", "Text", "MID(text, start, count)", "count characters from position start (the first character is 1).", "MID(reference, 3, 5)", "text"),
        new("LEN", "Text", "LEN(text)", "The number of characters.", "LEN(purpose)", "integer"),
        new("UPPER", "Text", "UPPER(text)", "The text in capital letters.", "UPPER(counterparty)", "text"),
        new("LOWER", "Text", "LOWER(text)", "The text in small letters.", "LOWER(email)", "text"),
        new("TRIM", "Text", "TRIM(text)", "The text without leading and trailing spaces.", "TRIM(counterparty)", "text"),
        new("REPLACE", "Text", "REPLACE(text, search, replacement)", "Replaces every occurrence of search (matching case exactly).", "REPLACE(iban, \" \", \"\")", "text"),
        new("SEARCH", "Text", "SEARCH(search, text)", "The position of search in text, ignoring case; 0 when it is not found.", "SEARCH(\"rewe\", counterparty)", "integer"),
        new("CONTAINS", "Text", "CONTAINS(text, search)", "True when text contains search, ignoring case.", "CONTAINS(purpose, \"miete\")", "boolean"),

        new("TODAY", "Date", "TODAY()", "Today's date (UTC).", "DATEDIFF(booked_on, TODAY(), \"days\")", "date"),
        new("NOW", "Date", "NOW()", "The current date and time (UTC, whole seconds).", "DATEDIFF(created_at, NOW(), \"days\")", "datetime"),
        new("YEAR", "Date", "YEAR(date)", "The year of a date.", "YEAR(booked_on)", "integer"),
        new("MONTH", "Date", "MONTH(date)", "The month of a date, 1–12.", "MONTH(booked_on)", "integer"),
        new("DAY", "Date", "DAY(date)", "The day of the month, 1–31.", "DAY(booked_on)", "integer"),
        new("HOUR", "Date", "HOUR(datetime)", "The hour, 0–23 (0 for dates).", "HOUR(recorded_at)", "integer"),
        new("MINUTE", "Date", "MINUTE(datetime)", "The minute, 0–59 (0 for dates).", "MINUTE(recorded_at)", "integer"),
        new("DATEDIFF", "Date", "DATEDIFF(start, end, unit)", "Whole days, weeks, months or years from start to end (negative when end is earlier). The time of day is ignored.", "DATEDIFF(booked_on, TODAY(), \"months\")", "integer"),
        new("DATEADD", "Date", "DATEADD(date, count, unit)", "Adds count days, weeks, months or years (count may be negative). Month ends stay in their month: Jan 31 + 1 month is Feb 28.", "DATEADD(booked_on, 1, \"months\")", "the type of date"),
        new("SUM", RelatedRows, "SUM(link.column [WHERE condition])", "Adds up a column of the related rows (0 when there are none), optionally only those meeting the condition. In dashboard measures: over the rows of the group, e.g. SUM(amount WHERE amount < 0).", "SUM(transactions.amount)", "the type of column"),
        new("COUNT", RelatedRows, "COUNT(link [WHERE condition])", "The number of related rows, or of their non-empty values with COUNT(link.column). In dashboard measures: COUNT() counts the group's rows.", "COUNT(transactions, transactions.amount < 0)", "integer"),
        new("AVG", RelatedRows, "AVG(link.column, [condition])", "The average of a column of the related rows; empty when there are none.", "AVG(transactions.amount)", "float or currency"),
        new("MIN", RelatedRows, "MIN(link.column, [condition])", "The smallest number or earliest date among the related rows.", "MIN(transactions.booked_on)", "the type of column"),
        new("MAX", RelatedRows, "MAX(link.column, [condition])", "The largest number or latest date among the related rows.", "MAX(transactions.booked_on)", "the type of column"),
        new("MEDIAN", RelatedRows, "MEDIAN(link.column [WHERE condition])", "The middle value of a column of the related rows (halfway between the two middle ones for an even count); empty when there are none. In dashboard measures: over the rows of the group.", "MEDIAN(transactions.amount)", "float or currency"),
        new("PERCENTILE", RelatedRows, "PERCENTILE(link.column, p [WHERE condition])", "The value p percent (0–100) of the related rows stay below, interpolated between neighbours; PERCENTILE(…, 50) is the median. In dashboard measures: over the rows of the group.", "PERCENTILE(transactions.amount, 90)", "float or currency"),
        new("RUNNING_SUM", Measures, "RUNNING_SUM(measure)", "Dashboards and queries: the measure added up over the date group, e.g. a balance from monthly sums. Wraps the whole formula.", "RUNNING_SUM(SUM(amount))", "the type of measure"),
        new("BUCKET_START", Measures, "BUCKET_START()", "In values at the end of each period: the first day of the period.", "SUM(amount WHERE booked_on >= BUCKET_START() AND booked_on <= BUCKET_END())", "date"),
        new("BUCKET_END", Measures, "BUCKET_END()", "In values at the end of each period: the last day of the period.", "SUM(amount WHERE booked_on <= BUCKET_END())", "date"),
        new("LOOKUP_ASOF", "Lookup", "LOOKUP_ASOF(table, match_column, match_value, date_column, date, value_column)", "The value of the latest row on or before the date, e.g. a price at a month end. Table and column names are text.", "LOOKUP_ASOF(\"prices\", \"ticker\", ticker, \"date\", TODAY(), \"price\")", "the type of value_column"),
        new("FV", "Finance", "FV(rate, periods, payment, [present_value], [at_start])", "The future value of regular payments at a fixed rate, like Excel: money paid out is negative. at_start: payments at the start of each period.", "FV(0.05 / 12, 10 * 12, -200)", "float"),
        new("PV", "Finance", "PV(rate, periods, payment, [future_value], [at_start])", "The present value of regular payments at a fixed rate, like Excel.", "PV(0.04 / 12, 20 * 12, -1000)", "float"),
        new("PMT", "Finance", "PMT(rate, periods, present_value, [future_value], [at_start])", "The payment per period for a loan or savings plan at a fixed rate, like Excel: a loan of 10000 gives a negative payment.", "PMT(0.035 / 12, 25 * 12, 300000)", "float"),
        new("NPV", "Finance", "NPV(rate, value1, value2, …)", "The net present value of flows at the end of periods 1, 2, …, like Excel.", "NPV(0.08, -1000, 300, 400, 500)", "float"),
        new("XIRR", Returns, "XIRR(amount, date) or XIRR(FLOWS(…), …, final_value, final_date)", "Dashboards: the annual rate of return of dated cash flows (paid out negative), like Excel. Over the rows of the group, from FLOWS of several tables, and with single flows such as today's value. Must be the whole measure.", "XIRR(FLOWS(-amount, booked_on), SUM(amount), TODAY())", "float"),
        new("IRR", Returns, "IRR(amount, date)", "Dashboards: the rate per period of flows at equal intervals, in date order, like Excel. Must be the whole measure.", "IRR(amount, booked_on)", "float"),
        new("FLOWS", Returns, "FLOWS(amount, date [WHERE condition])", "The cash flows of the group's rows, or of each row's related rows (FLOWS(trades.amount, trades.date)), for XIRR and IRR.", "XIRR(FLOWS(amount, booked_on WHERE amount <> 0))", "flows"),
        new("TWR", Returns, "TWR(value, flow, [index_start])", "Dashboards, values at the end of each period: the time-weighted return so far — each period's growth value / (previous value + money put in), multiplied. With index_start (e.g. 100) an index instead. Must be the whole measure.", "TWR(SUM(amount WHERE booked_on <= BUCKET_END()), SUM(amount WHERE booked_on >= BUCKET_START() AND booked_on <= BUCKET_END()), 100)", "float"),
        new("REBASE", Returns, "REBASE(measure, [start])", "Dashboards: the measure relative to its first value in the range, which becomes 100 (or start) — to compare a fund with a benchmark. Must be the whole measure.", "REBASE(SUM(amount), 100)", "float"),
        new("CUSTOM", "Custom", "CUSTOM(\"name\", argument1, …)", "Runs one of your own JavaScript functions (see Functions). It must be the whole formula: in a computed column the value is stored and recalculated in the background; in dashboards it runs on each result.", "CUSTOM(\"risk_class\", volatility)", "the function's result type"),
        new("CUSTOM_ROWS", "Custom", "CUSTOM_ROWS(\"name\", PARTITION(column, …), ORDER(column, …), argument1, …)", "Computed columns: runs one of your functions over a group of rows at once, in order, and gives each row its value — e.g. realised gains by average cost per security. Rows with the same PARTITION values form a group; ORDER sorts them (then the key). The function must run over a group of rows (see Functions); it must be the whole formula.", "CUSTOM_ROWS(\"avg_cost\", PARTITION(account_id, isin), ORDER(date, id), shares, price, fees, \"realized\")", "the function's result type"),
        new("PARTITION", "Custom", "PARTITION(column, …)", "Part of CUSTOM_ROWS: rows with the same values form one group.", "CUSTOM_ROWS(\"avg_cost\", PARTITION(account_id, isin), ORDER(date), shares, price, fees, \"cost\")", "—"),
        new("ORDER", "Custom", "ORDER(column, …)", "Part of CUSTOM_ROWS: the order in which the function sees a group's rows, ascending, empty values first; the key breaks ties.", "CUSTOM_ROWS(\"avg_cost\", PARTITION(isin), ORDER(date, id), shares, price, fees, \"held\")", "—"),
        new("SUMIF", RelatedRows, "SUMIF(link.column, condition)", "SUM with a condition.", "SUMIF(transactions.amount, transactions.amount < 0)", "the type of column"),
        new("COUNTIF", RelatedRows, "COUNTIF(link, condition)", "COUNT with a condition.", "COUNTIF(transactions, transactions.amount < 0)", "integer"),
        new("AVGIF", RelatedRows, "AVGIF(link.column, condition)", "AVG with a condition.", "AVGIF(transactions.amount, transactions.amount < 0)", "float or currency"),
    ];

    public const string RelatedRows = "Related rows";
    public const string Measures = "Measures";
    public const string Returns = "Returns";

    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CEILING"] = "CEIL"
    };

    private static readonly Dictionary<string, FormulaFunctionInfo> ByName =
        Catalog.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    public static FormulaFunctionInfo? Find(string name) =>
        ByName.GetValueOrDefault(Aliases.GetValueOrDefault(name, name));

    /// <summary>The keywords and literals the editor highlights.</summary>
    public static readonly IReadOnlyList<string> Keywords = ["AND", "OR", "NOT", "TRUE", "FALSE", "NULL"];
}
