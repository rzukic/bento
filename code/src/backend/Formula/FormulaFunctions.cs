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
    ];

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
