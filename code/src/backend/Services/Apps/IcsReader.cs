using System.Globalization;
using System.Text;

namespace Bento.Api.Services.Apps;

/// <summary>
/// Reads iCalendar (RFC 5545) as calendar programs send it: unfolds lines,
/// splits properties into name, parameters and value, and nests components
/// (VCALENDAR → VEVENT → VALARM). Lenient: bare LF line ends, lower-case names.
/// </summary>
public static class IcsReader
{
    public sealed record Property(string Name, IReadOnlyDictionary<string, string> Parameters, string Value, string Line)
    {
        public string? Parameter(string name) => Parameters.TryGetValue(name, out var value) ? value : null;
    }

    public sealed class Component(string name)
    {
        public string Name { get; } = name;
        public List<Property> Properties { get; } = [];
        public List<Component> Children { get; } = [];

        public Property? First(string name) => Properties.FirstOrDefault(p => p.Name == name);

        public IEnumerable<Component> All(string name) => Children.Where(c => c.Name == name);
    }

    /// <summary>The top components of the text (usually one VCALENDAR); null when it is not iCalendar.</summary>
    public static Component? Parse(string text)
    {
        var lines = Unfold(text);
        var root = new Component("ROOT");
        var stack = new Stack<Component>();
        stack.Push(root);
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;
            var property = ParseLine(line);
            if (property is null)
                continue;
            if (property.Name == "BEGIN")
            {
                var component = new Component(property.Value.Trim().ToUpperInvariant());
                stack.Peek().Children.Add(component);
                stack.Push(component);
            }
            else if (property.Name == "END")
            {
                if (stack.Count > 1)
                    stack.Pop();
            }
            else
            {
                stack.Peek().Properties.Add(property);
            }
        }
        return root.Children.FirstOrDefault(c => c.Name == "VCALENDAR");
    }

    public static List<string> Unfold(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (current.Length > 0)
                result.Add(current.ToString());
            current.Clear().Append(raw);
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static Property? ParseLine(string line)
    {
        // name *(";" param) ":" value — a colon inside a quoted parameter value does not end the name part.
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0)
            return null;
        var head = line[..colon];
        var value = line[(colon + 1)..];
        var parts = SplitParameters(head);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals > 0)
                parameters[part[..equals].Trim()] = part[(equals + 1)..].Trim().Trim('"');
        }
        return new Property(parts[0].Trim().ToUpperInvariant(), parameters, value, line);
    }

    private static List<string> SplitParameters(string head)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in head)
        {
            if (c == '"')
                quoted = !quoted;
            if (c == ';' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>A TEXT value with its escapes resolved.</summary>
    public static string Unescape(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                text.Append(next switch { 'n' or 'N' => '\n', _ => next });
                continue;
            }
            text.Append(value[i]);
        }
        return text.ToString();
    }

    /// <summary>
    /// A DATE or DATE-TIME property as an event time: a date for VALUE=DATE;
    /// a moment for UTC (Z) or a TZID the server knows; a time without either
    /// ("floating") counts as the workspace's wall time.
    /// </summary>
    public static CalendarEvents.EventTime? Time(Property? property, TimeZoneInfo workspace)
    {
        if (property is null)
            return null;
        var value = property.Value.Trim();
        if (property.Parameter("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true || value.Length == 8)
            return DateOnly.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? new CalendarEvents.EventTime(day, null)
                : null;
        var utc = value.EndsWith('Z') || value.EndsWith('z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z', 'z'), ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return null;
        if (utc)
            return new CalendarEvents.EventTime(null, new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Utc)));
        var zone = property.Parameter("TZID") is { } id ? Zone(id) ?? workspace : workspace;
        var local = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return new CalendarEvents.EventTime(null, new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime());
    }

    /// <summary>An IANA zone, or a Windows one (Outlook sends "W. Europe Standard Time"); null when unknown.</summary>
    private static TimeZoneInfo? Zone(string id)
    {
        var name = id.Trim().Trim('"');
        // Some programs prefix the zone with a path ("/mozilla.org/20070129_1/Europe/Berlin").
        var slash = name.IndexOf('/', 1);
        foreach (var candidate in new[] { name, name.StartsWith('/') && slash > 0 ? string.Join('/', name.Split('/').TakeLast(2)) : name })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone))
                return zone;
            try
            {
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(candidate, out var iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out zone))
                    return zone;
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>A DURATION value (P1D, PT1H30M, -PT15M) as a time span; null when unreadable.</summary>
    public static TimeSpan? Duration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim().ToUpperInvariant();
        var negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        if (!text.StartsWith('P'))
            return null;
        var total = TimeSpan.Zero;
        var number = new StringBuilder();
        foreach (var c in text[1..])
        {
            if (char.IsDigit(c))
            {
                number.Append(c);
                continue;
            }
            if (c == 'T')
                continue;
            if (number.Length == 0)
                return null;
            var n = int.Parse(number.ToString(), CultureInfo.InvariantCulture);
            number.Clear();
            total += c switch
            {
                'W' => TimeSpan.FromDays(7 * n),
                'D' => TimeSpan.FromDays(n),
                'H' => TimeSpan.FromHours(n),
                'M' => TimeSpan.FromMinutes(n),
                'S' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero
            };
        }
        return negative ? -total : total;
    }
}
