using System.Globalization;
using System.Text.RegularExpressions;

namespace Bento.Api.Formula;

/// <summary>An argument of a custom function: its name in the code and the type of value it gets.</summary>
public sealed record CustomFunctionArgument(string Name, string Type);

/// <summary>
/// A custom function (Step 4.1, Part 5 R5.5): JavaScript the user writes as a
/// function body, called from formulas with <c>CUSTOM("name", …)</c>.
/// Types use the formula type names (integer, float, currency, text, boolean,
/// date, datetime); an argument may also be <c>any</c>.
/// </summary>
public sealed record CustomFunctionDef(
    string Name,
    string? Description,
    IReadOnlyList<CustomFunctionArgument> Arguments,
    string ResultType,
    string Code,
    int Version)
{
    public FormulaType Result => FormulaTypes.FromName(ResultType) ?? FormulaType.Text;
}

/// <summary>Names, types and the rules both the API and the compiler check.</summary>
public static partial class CustomFunctionRules
{
    public const int MaxArguments = 10;
    public const int MaxCodeLength = 50_000;

    public static readonly IReadOnlyList<string> ResultTypes = ["float", "integer", "currency", "text", "boolean", "date", "datetime"];
    public static readonly IReadOnlyList<string> ArgumentTypes = ["float", "integer", "currency", "text", "boolean", "date", "datetime", "any"];

    /// <summary>JavaScript words an argument cannot be called.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else",
        "export", "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new",
        "null", "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while",
        "with", "yield", "let", "static", "implements", "interface", "package", "private", "protected", "public",
        "await", "enum", "arguments", "eval", "console"
    };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]{0,62}$")]
    private static partial Regex ArgumentPattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static bool IsValidArgumentName(string name) =>
        ArgumentPattern().IsMatch(name) && !Reserved.Contains(name) && !name.StartsWith("__", StringComparison.Ordinal);

    /// <summary>A type name in the canonical spelling ("number" → "float"), or null when unknown.</summary>
    public static string? NormalizeType(string? type, bool argument)
    {
        var trimmed = type?.Trim().ToLowerInvariant();
        if (argument && trimmed == "any")
            return "any";
        return FormulaTypes.FromName(trimmed) is { } known ? FormulaTypes.Name(known) : null;
    }

    /// <summary>Whether a formula value of this type may be passed to an argument.</summary>
    public static bool Accepts(string argumentType, FormulaType actual) =>
        actual == FormulaType.Null || argumentType switch
        {
            "any" => true,
            "float" or "currency" or "integer" => FormulaTypes.IsNumeric(actual),
            "date" => actual is FormulaType.Date or FormulaType.DateTime,
            "datetime" => actual is FormulaType.Date or FormulaType.DateTime,
            "boolean" => actual == FormulaType.Boolean,
            _ => true // text: numbers, dates and yes/no are passed as their text
        };
}

/// <summary>Custom function values as text, for the stored values of computed columns and for the sandbox.</summary>
public static class CustomFunctionValues
{
    public static string? DateText(object? value) => value switch
    {
        null or DBNull => null,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime stamp => stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string text when text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => null
    };

    public static string? DateTimeText(object? value) => value switch
    {
        null or DBNull => null,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z",
        DateTime stamp => DateTime.SpecifyKind(stamp, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            => parsed.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        _ => null
    };

    /// <summary>A result as stored text that <see cref="ReadSql"/> turns back into the formula type on each provider.</summary>
    public static string? StorageText(object? value, FormulaType type, bool postgres)
    {
        if (value is null or DBNull)
            return null;
        return type switch
        {
            FormulaType.Integer => System.Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            FormulaType.Number or FormulaType.Currency => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            FormulaType.Boolean => value is true ? (postgres ? "true" : "1") : (postgres ? "false" : "0"),
            FormulaType.Date => DateText(value),
            FormulaType.DateTime => DateTimeText(value) is { } stamp ? stamp[..10] + " " + stamp[11..19] : null,
            _ => System.Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>Stored text as a value of the formula type, like the compiler's own SQL for that type.</summary>
    public static string ReadSql(string sql, FormulaType type, bool postgres) => type switch
    {
        FormulaType.Integer => postgres ? $"CAST({sql} AS BIGINT)" : $"CAST({sql} AS INTEGER)",
        FormulaType.Number or FormulaType.Currency => postgres ? $"CAST({sql} AS NUMERIC)" : $"CAST({sql} AS REAL)",
        FormulaType.Boolean => postgres ? $"CAST({sql} AS BOOLEAN)" : $"CAST({sql} AS INTEGER)",
        FormulaType.Date => postgres ? $"CAST({sql} AS DATE)" : sql,
        FormulaType.DateTime => postgres ? $"CAST({sql} AS TIMESTAMP)" : sql,
        _ => sql
    };
}

/// <summary>The custom functions formulas can call, by name (case-insensitive).</summary>
public interface ICustomFunctionCatalog
{
    /// <summary>Changes whenever a function is saved or deleted.</summary>
    long Version { get; }

    IReadOnlyDictionary<string, CustomFunctionDef> Functions { get; }
}

/// <summary>A fixed set of functions (tests, drafts).</summary>
public sealed class StaticFunctionCatalog(IEnumerable<CustomFunctionDef> functions, long version = 1) : ICustomFunctionCatalog
{
    public static readonly StaticFunctionCatalog Empty = new([]);

    public long Version { get; } = version;

    public IReadOnlyDictionary<string, CustomFunctionDef> Functions { get; } =
        functions.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
}
