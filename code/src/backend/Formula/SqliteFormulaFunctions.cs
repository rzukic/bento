using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Bento.Api.Formula;

/// <summary>
/// Functions formulas need on SQLite to match Postgres: SQLite's own
/// <c>lower</c>/<c>upper</c> only change ASCII letters ("Ä" stays "Ä"), and
/// casting a REAL to text gives "2.0" or "1.0e+20" where Postgres gives "2".
/// Registered on every data connection.
/// </summary>
public static class SqliteFormulaFunctions
{
    public static void Register(SqliteConnection connection)
    {
        connection.CreateFunction("bento_lower", (string? text) => text?.ToLowerInvariant(), isDeterministic: true);
        connection.CreateFunction("bento_upper", (string? text) => text?.ToUpperInvariant(), isDeterministic: true);
        connection.CreateFunction("bento_num_text", (double? value) => NumberText(value), isDeterministic: true);
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
