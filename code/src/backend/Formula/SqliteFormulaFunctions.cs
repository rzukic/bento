using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Bento.Api.Formula;

/// <summary>
/// Functions formulas need on SQLite to match Postgres: SQLite's own
/// <c>lower</c>/<c>upper</c> only change ASCII letters ("Ä" stays "Ä"), and
/// casting a REAL to text gives "2.0" or "1.0e+20" where Postgres gives "2".
/// Registered on every data connection. <c>bento_today()</c> is TODAY() in the
/// workspace's time zone, which SQLite does not know.
/// </summary>
public static class SqliteFormulaFunctions
{
    public static void Register(SqliteConnection connection, TimeZoneInfo? zone = null)
    {
        var today = zone ?? TimeZoneInfo.Utc;
        connection.CreateFunction("bento_today",
            () => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            isDeterministic: false);
        connection.CreateFunction("bento_lower", (string? text) => text?.ToLowerInvariant(), isDeterministic: true);
        connection.CreateFunction("bento_upper", (string? text) => text?.ToUpperInvariant(), isDeterministic: true);
        connection.CreateFunction("bento_num_text", (double? value) => NumberText(value), isDeterministic: true);
        // percentile_cont on SQLite. The seed stays null: every group starts its own list.
        connection.CreateAggregate<double?, double, PercentileValues?, double?>(
            "bento_percentile",
            null,
            (values, value, fraction) =>
            {
                values ??= new PercentileValues(fraction);
                if (value is { } number)
                    values.Numbers.Add(number);
                return values;
            },
            values => values is null ? null : Percentile(values.Numbers, values.Fraction),
            isDeterministic: true);
        // LAST/FIRST over a group: the value of the row with the latest (earliest) order value.
        connection.CreateAggregate<object?, object?, Picked?, object?>(
            "bento_last", null, (picked, value, order) => Pick(picked, value, order, latest: true), picked => picked?.Value, isDeterministic: true);
        connection.CreateAggregate<object?, object?, Picked?, object?>(
            "bento_first", null, (picked, value, order) => Pick(picked, value, order, latest: false), picked => picked?.Value, isDeterministic: true);
    }

    public sealed record Picked(object? Value, object Order);

    private static Picked? Pick(Picked? picked, object? value, object? order, bool latest)
    {
        if (order is null)
            return picked;
        if (picked is null)
            return new Picked(value, order);
        var compared = CompareOrder(order, picked.Order);
        return (latest ? compared > 0 : compared < 0) ? new Picked(value, order) : picked;
    }

    /// <summary>Numbers by value, everything else (dates are yyyy-MM-dd text) in character order, as SQLite sorts.</summary>
    private static int CompareOrder(object a, object b) =>
        a is long or double && b is long or double
            ? Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture))
            : string.CompareOrdinal(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));

    public sealed record PercentileValues(double Fraction)
    {
        public List<double> Numbers { get; } = [];
    }

    /// <summary>
    /// Postgres' <c>percentile_cont</c>: the value at <paramref name="fraction"/> (0–1) of the
    /// sorted values, interpolated linearly between neighbours; empty without values.
    /// </summary>
    public static double? Percentile(List<double> numbers, double fraction)
    {
        if (numbers.Count == 0)
            return null;
        numbers.Sort();
        var position = fraction * (numbers.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var first = numbers[lower];
        return lower == upper ? first : first + (numbers[upper] - first) * (position - lower);
    }

    /// <summary>
    /// Rounded to 10 places without trailing zeros, like Postgres'
    /// <c>trim_scale(round(x::numeric, 10))::text</c> (both read a double with 15 significant digits).
    /// </summary>
    public static string? NumberText(double? value)
    {
        if (value is not { } number)
            return null;
        if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) >= 7.9e27)
            return number.ToString("R", CultureInfo.InvariantCulture);
        var rounded = Math.Round((decimal)number, 10, MidpointRounding.AwayFromZero);
        return (rounded / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
    }
}
