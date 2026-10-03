using System.Globalization;
using Bento.Api.Db;
using Bento.Api.Formula;

namespace Bento.Api.Services;

/// <summary>
/// Turns values read from SQLite or Postgres into one JSON shape: dates as
/// <c>yyyy-MM-dd</c>, date-times as UTC with Z, numbers without float noise
/// or trailing zeros. Shared by aggregation queries and formulas.
/// </summary>
internal static class ResultValues
{
    public static object? Shape(object? value, ColumnType type)
    {
        if (value is null or DBNull)
            return null;
        return type switch
        {
            ColumnType.Integer or ColumnType.Reference => value is string ? value : Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ColumnType.Float or ColumnType.Currency => Normalize(Math.Round(ToDecimal(value)!.Value, type == ColumnType.Currency ? 4 : 10, MidpointRounding.AwayFromZero)),
            ColumnType.Boolean => value is bool flag ? flag : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
            ColumnType.Date => value switch
            {
                DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            },
            ColumnType.DateTime => value switch
            {
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (dateTime.Kind == DateTimeKind.Utc ? "Z" : ""),
                DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            },
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>A formula result: date-times are UTC wall time on both providers, so they always get a Z.</summary>
    public static object? Shape(object? value, FormulaType type)
    {
        if (value is null or DBNull)
            return null;
        return type switch
        {
            FormulaType.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            FormulaType.Number => Shape(value, ColumnType.Float),
            FormulaType.Currency => Shape(value, ColumnType.Currency),
            FormulaType.Boolean => Shape(value, ColumnType.Boolean),
            FormulaType.Date => Shape(value, ColumnType.Date),
            FormulaType.DateTime => value switch
            {
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    => parsed.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            },
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    public static decimal? ToDecimal(object? value) => value switch
    {
        null or DBNull => null,
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        _ => decimal.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, NumberStyles.Float, CultureInfo.InvariantCulture)
    };

    /// <summary>-49.1000 → -49.1 (no trailing zeros in the JSON).</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}
