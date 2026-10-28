using System.Globalization;

namespace Bento.Api.Services.Notifications;

/// <summary>
/// Numbers in notifications as the workspace writes them (Settings → Workspace):
/// its locale's separators, its currency. The server runs without culture data
/// (InvariantGlobalization), so the common styles are written down here; the
/// browser formats the same numbers with Intl. Without a workspace locale
/// (each browser its own) numbers look English: €1,250.00.
/// </summary>
public static class MessageFormat
{
    /// <param name="SymbolFirst">€1,250.00 / € 1.250,00 rather than 1.250,00 €.</param>
    /// <param name="Space">A space between symbol and number.</param>
    private sealed record Style(string Group, string Decimal, bool SymbolFirst, bool Space);

    private static readonly Style English = new(",", ".", true, false);
    private const string Thin = " ";
    private const string NoBreak = " ";

    private static Style StyleFor(string? locale)
    {
        var tag = locale?.Replace('_', '-') ?? "";
        var language = tag.Split('-')[0].ToLowerInvariant();
        return tag.ToUpperInvariant() switch
        {
            "DE-CH" or "FR-CH" or "IT-CH" => new Style("’", ".", true, true),
            "DE-AT" or "NL-NL" or "NL-BE" => new Style(".", ",", true, true),
            _ => language switch
            {
                "fr" => new Style(Thin, ",", false, true),
                "de" or "es" or "it" or "pt" or "id" or "tr" or "nl" or "da" => new Style(".", ",", false, true),
                "pl" or "cs" or "sk" or "sv" or "nb" or "nn" or "no" or "fi" or "hu" or "ru" or "uk" => new Style(NoBreak, ",", false, true),
                _ => English
            }
        };
    }

    /// <param name="type">The value's type as formulas name it: currency, integer, number, percent …</param>
    public static string Format(object? value, string? type, WorkspaceSettings.Values workspace)
    {
        if (value is null)
            return "—";
        if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var style = StyleFor(workspace.Locale);
        return type switch
        {
            "currency" => Currency(number, workspace.Currency, style),
            "integer" => Number(number, 0, 0, style),
            "percent" => Number(number * 100, 0, 1, style) + (style == English ? "%" : " %"),
            _ => Number(number, 0, 2, style)
        };
    }

    public static string Amount(decimal value, WorkspaceSettings.Values workspace) => Format(value, "currency", workspace);

    private static string Currency(decimal value, string currency, Style style)
    {
        var symbol = Symbol(currency);
        var digits = Number(Math.Abs(value), 2, 2, style);
        var sign = value < 0 ? "-" : "";
        var gap = style.Space ? NoBreak : "";
        return style.SymbolFirst ? $"{sign}{symbol}{gap}{digits}" : $"{sign}{digits}{gap}{symbol}";
    }

    private static string Number(decimal value, int minDecimals, int maxDecimals, Style style)
    {
        var rounded = Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero);
        var pattern = maxDecimals == 0 ? "0" : "0." + new string('0', minDecimals) + new string('#', maxDecimals - minDecimals);
        var text = Math.Abs(rounded).ToString(pattern, CultureInfo.InvariantCulture);
        var parts = text.Split('.');
        var whole = parts[0];
        var grouped = new System.Text.StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0)
                grouped.Append(style.Group);
            grouped.Append(whole[i]);
        }
        if (parts.Length > 1)
            grouped.Append(style.Decimal).Append(parts[1]);
        return (rounded < 0 ? "-" : "") + grouped;
    }

    /// <summary>€ for EUR, $ for USD … the code itself for others (CHF, SEK).</summary>
    public static string Symbol(string currency) => currency.ToUpperInvariant() switch
    {
        "EUR" => "€",
        "USD" => "$",
        "GBP" => "£",
        "JPY" => "¥",
        "PLN" => "zł",
        "CZK" => "Kč",
        "HUF" => "Ft",
        var code => code
    };
}
