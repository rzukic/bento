using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bento.Api.Db;

namespace Bento.Api.Services.Import;

/// <summary>
/// How numbers and dates are written in a file: <c>1.234,56</c> and
/// <c>31.12.2026</c> in German exports, <c>1,234.56</c> and <c>2026-12-31</c>
/// elsewhere.
/// </summary>
public sealed record ImportLocale(string DecimalSeparator, string DateFormat)
{
    public static readonly ImportLocale German = new(",", "dd.MM.yyyy");
    public static readonly ImportLocale Iso = new(".", "yyyy-MM-dd");

    public static IReadOnlyList<string> DateFormats { get; } =
        ["dd.MM.yyyy", "dd.MM.yy", "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd-MM-yyyy"];

    public bool UsesDecimalComma => DecimalSeparator == ",";
}

/// <summary>
/// Turns the raw text of a CSV cell into the value a column stores, using the
/// file's <see cref="ImportLocale"/>. Results are in the shapes the CRUD API
/// accepts: numbers, <c>yyyy-MM-dd</c> dates, <c>yyyy-MM-ddTHH:mm:ss</c>
/// date-times, booleans and trimmed text.
/// </summary>
public static partial class ImportValueParser
{
    private static readonly string[] CurrencyMarkers = ["EUR", "USD", "CHF", "GBP", "€", "$", "£"];

    private static readonly HashSet<string> TrueWords = new(StringComparer.OrdinalIgnoreCase)
        { "true", "ja", "yes", "y", "j", "x", "1", "wahr" };

    private static readonly HashSet<string> FalseWords = new(StringComparer.OrdinalIgnoreCase)
        { "false", "nein", "no", "n", "0", "falsch" };

    /// <summary>True when the cell is empty after trimming.</summary>
    public static bool IsBlank(string? raw) => string.IsNullOrWhiteSpace(raw);

    /// <summary>
    /// Parses a number written with the locale's decimal separator. Accepts
    /// thousands separators, a currency symbol or code, a leading <c>+</c>, a
    /// trailing minus (<c>12,50-</c>) and parentheses for negatives.
    /// </summary>
    public static bool TryParseNumber(string raw, ImportLocale locale, out decimal value)
    {
        value = 0;
        var text = raw.Trim();
        foreach (var marker in CurrencyMarkers)
            text = text.Replace(marker, "", StringComparison.OrdinalIgnoreCase);
        text = RemoveSpaces(text);
        if (text.Length == 0)
            return false;

        var negative = false;
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            negative = true;
            text = text[1..^1];
        }
        if (text.EndsWith('-'))
        {
            negative = !negative;
            text = text[..^1];
        }
        if (text.StartsWith('-'))
        {
            negative = !negative;
            text = text[1..];
        }
        else if (text.StartsWith('+'))
        {
            text = text[1..];
        }

        var thousands = locale.UsesDecimalComma ? '.' : ',';
        var decimalMark = locale.UsesDecimalComma ? ',' : '.';
        if (text.Contains(thousands) && !ThousandsGrouping(thousands).IsMatch(text.Split(decimalMark)[0]))
            return false;

        text = text.Replace(thousands.ToString(), "").Replace("'", "");
        if (decimalMark == ',')
            text = text.Replace(',', '.');

        if (!PlainNumber().IsMatch(text))
            return false;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        if (negative)
            value = -value;
        return true;
    }

    /// <summary>Parses a date in the locale's format (one-digit days and months allowed) or ISO form.</summary>
    public static bool TryParseDate(string raw, ImportLocale locale, out DateOnly value)
    {
        var text = raw.Trim();
        foreach (var format in DateFormatsFor(locale.DateFormat))
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                return true;
        }
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>
    /// Parses a date with an optional time (<c>31.12.2026 14:05</c>) or an ISO
    /// timestamp. Wall time is kept as written; a trailing <c>Z</c> or offset is
    /// converted to UTC and marked with <c>Z</c>.
    /// </summary>
    public static bool TryParseDateTime(string raw, ImportLocale locale, out string value)
    {
        value = "";
        var text = raw.Trim();

        if (IsoWithZone().IsMatch(text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var zoned))
        {
            value = zoned.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            return true;
        }

        var parts = text.Split([' ', 'T'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !TryParseDate(parts[0], locale, out var date))
            return false;

        var time = TimeOnly.MinValue;
        if (parts.Length == 2 &&
            !TimeOnly.TryParseExact(parts[1].Trim(), ["HH:mm:ss", "HH:mm", "H:mm", "H:mm:ss", "HH:mm:ss.fff"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return false;

        value = date.ToDateTime(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryParseBoolean(string raw, out bool value)
    {
        var text = raw.Trim();
        if (TrueWords.Contains(text))
        {
            value = true;
            return true;
        }
        value = false;
        return FalseWords.Contains(text);
    }

    /// <summary>
    /// The column type a set of sample values suggests, for creating a table
    /// from a file and for hints in the mapping step.
    /// </summary>
    public static ColumnType InferType(IEnumerable<string> samples, ImportLocale locale)
    {
        var values = samples.Where(sample => !IsBlank(sample)).Select(sample => sample.Trim()).ToList();
        if (values.Count == 0)
            return ColumnType.Text;
        if (values.All(v => TryParseDate(v, locale, out _)))
            return ColumnType.Date;
        if (values.All(v => TryParseDateTime(v, locale, out _)))
            return ColumnType.DateTime;
        if (values.All(v => TryParseNumber(v, locale, out var n) && n == decimal.Truncate(n) && !v.Contains(locale.DecimalSeparator)))
            return values.Any(v => CurrencyMarkers.Any(m => v.Contains(m, StringComparison.OrdinalIgnoreCase)))
                ? ColumnType.Currency
                : ColumnType.Integer;
        if (values.All(v => TryParseNumber(v, locale, out _)))
            return values.Any(v => CurrencyMarkers.Any(m => v.Contains(m, StringComparison.OrdinalIgnoreCase)))
                ? ColumnType.Currency
                : ColumnType.Float;
        if (values.All(v => TryParseBoolean(v, out _)) && values.Any(v => !PlainNumber().IsMatch(v)))
            return ColumnType.Boolean;
        return ColumnType.Text;
    }

    /// <summary>
    /// Guesses how a file writes numbers and dates from its cells: a decimal
    /// comma when amounts look like <c>-1.234,56</c>, and the first date format
    /// that reads every date-looking cell.
    /// </summary>
    public static ImportLocale DetectLocale(IEnumerable<string> cells)
    {
        var values = cells.Where(c => !IsBlank(c)).Select(c => c.Trim()).ToList();
        var commaDecimals = values.Count(v => CommaDecimal().IsMatch(RemoveSpaces(v)));
        var dotDecimals = values.Count(v => DotDecimal().IsMatch(RemoveSpaces(v)));
        var decimalSeparator = commaDecimals > dotDecimals ? "," : ".";

        var dateLike = values.Where(v => DateLike().IsMatch(v)).Select(v => v.Split(' ', 'T')[0]).ToList();
        var dateFormat = decimalSeparator == "," ? "dd.MM.yyyy" : "yyyy-MM-dd";
        if (dateLike.Count > 0)
        {
            var match = ImportLocale.DateFormats.FirstOrDefault(format =>
                dateLike.All(v => TryParseDate(v, new ImportLocale(decimalSeparator, format), out _) &&
                                  DateFormatsFor(format).Any(f => DateOnly.TryParseExact(v, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))));
            if (match is not null)
                dateFormat = match;
        }

        return new ImportLocale(decimalSeparator, dateFormat);
    }

    /// <summary>The format plus its variant with one-digit day and month.</summary>
    private static IEnumerable<string> DateFormatsFor(string format)
    {
        yield return format;
        var loose = format.Replace("dd", "d").Replace("MM", "M");
        if (loose != format)
            yield return loose;
    }

    private static string RemoveSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            // Regular, no-break and narrow no-break spaces all appear as thousands separators.
            if (!char.IsWhiteSpace(character) && character != ' ' && character != ' ')
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static Regex ThousandsGrouping(char separator) =>
        new($"^\\d{{1,3}}(\\{separator}\\d{{3}})+$", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^\d+(\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();

    [GeneratedRegex(@"^[-+(]?\d{1,3}(\.\d{3})*,\d+\)?-?$|^[-+(]?\d+,\d+\)?-?$", RegexOptions.CultureInvariant)]
    private static partial Regex CommaDecimal();

    [GeneratedRegex(@"^[-+(]?\d{1,3}(,\d{3})*\.\d+\)?-?$|^[-+(]?\d+\.\d+\)?-?$", RegexOptions.CultureInvariant)]
    private static partial Regex DotDecimal();

    [GeneratedRegex(@"^\d{1,4}[./-]\d{1,2}[./-]\d{2,4}([ T]\d{1,2}:\d{2}(:\d{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateLike();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoWithZone();
}
