using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bento.Api.Exceptions;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Services;

/// <summary>
/// Installs a template (Step 5.15) as the one who asks: each step is a call to
/// Bento's own API with their credentials — the schema, the function it
/// needs, example rows, dashboards, forms, and the note for the AI — so the
/// same checks and permissions apply as when they do it by hand. Nothing
/// existing changes; taken table names need a prefix.
/// </summary>
public sealed partial class TemplateInstaller
{
    private const string Api = "/api/v1";

    private readonly TemplateCatalog _catalog;
    private readonly BentoSelfClient _api;
    private readonly WorkspaceSettings _settings;

    public TemplateInstaller(TemplateCatalog catalog, BentoSelfClient api, WorkspaceSettings settings)
    {
        _catalog = catalog;
        _api = api;
        _settings = settings;
    }

    public sealed record InstalledItem(long Id, string Name);

    /// <param name="Open">The dashboard to open first.</param>
    public sealed record InstallResult(string Template, Dictionary<string, string> Tables, IReadOnlyList<InstalledItem> Dashboards,
        IReadOnlyList<InstalledItem> Forms, int ExampleRows, long? Open);

    /// <summary>The permissions an install needs from a connection or key (besides reading).</summary>
    public IReadOnlyList<string> Needs(string id, bool examples)
    {
        var recipe = Require(id);
        var needs = new List<string> { McpPermissions.Schema, McpPermissions.Dashboards, McpPermissions.Forms };
        if (examples && recipe["examples"] is JsonArray { Count: > 0 })
            needs.Add(McpPermissions.WriteRows);
        if (recipe["functions"] is JsonArray { Count: > 0 })
            needs.Add(McpPermissions.Functions);
        return needs;
    }

    public async Task<InstallResult> InstallAsync(string id, string? prefix, bool examples, BentoSelfClient.Credentials credentials, CancellationToken ct)
    {
        var recipe = Require(id);
        var clean = (prefix ?? "").Trim().ToLowerInvariant();
        if (clean.Length > 0 && !Prefix().IsMatch(clean))
            throw Invalid("prefix", "A prefix is lower-case letters, digits and underscores, e.g. work_.");
        var names = recipe["base"]!.AsObject().ToDictionary(p => p.Key, p => clean + p.Value!.GetValue<string>());

        // The model as it is: no table of the template may exist yet.
        var model = (await CallAsync(HttpMethod.Get, $"{Api}/admin/schema/model", credentials, null, ct)).AsObject();
        var tables = model["tables"] as JsonArray ?? [];
        var taken = tables.Select(t => t?["name"]?.GetValue<string>()?.ToLowerInvariant()).Where(n => n is not null).ToHashSet();
        var conflicts = names.Values.Where(n => taken.Contains(n.ToLowerInvariant())).ToList();
        if (conflicts.Count > 0)
            throw new ApiConflictException($"These tables exist already: {string.Join(", ", conflicts)}. Install with a prefix, e.g. \"{SuggestPrefix(recipe, taken)}\".",
                new Dictionary<string, object?> { ["field"] = "prefix", ["tables"] = conflicts });

        // Functions it needs (finance: avg_cost) — the user's own, from Bento's function templates.
        foreach (var function in recipe["functions"] as JsonArray ?? [])
            await EnsureFunctionAsync(function!.AsObject(), credentials, ct);

        // The tables and their links, added to the model; relation names kept unique.
        var relations = model["relations"] as JsonArray ?? [];
        var relationNames = relations.Select(r => r?["name"]?.GetValue<string>()?.ToLowerInvariant()).Where(n => n is not null).ToHashSet();
        foreach (var table in Fill(recipe["tables"]!, names).AsArray())
        {
            if (clean.Length > 0 && table!["displayName"] is JsonValue display)
                table["displayName"] = PrefixLabel(clean, display.GetValue<string>());
            tables.Add(table!.DeepClone());
        }
        foreach (var relation in Fill(recipe["relations"] ?? new JsonArray(), names).AsArray())
        {
            var name = relation!["name"]!.GetValue<string>();
            var unique = name;
            for (var i = 2; relationNames.Contains(unique.ToLowerInvariant()); i++)
                unique = $"{name}_{i}";
            relationNames.Add(unique.ToLowerInvariant());
            relation["name"] = unique;
            relations.Add(relation.DeepClone());
        }
        model["tables"] = tables.DeepClone();
        model["relations"] = relations.DeepClone();
        var preview = await CallAsync(HttpMethod.Post, $"{Api}/admin/schema/preview", credentials, model, ct);
        if (preview["isValid"]?.GetValue<bool>() == false)
            throw Invalid("template", string.Join(" ", (preview["errors"] as JsonArray ?? []).Select(e => e?["message"]?.GetValue<string>())));
        await CallAsync(HttpMethod.Put, $"{Api}/admin/schema", credentials, model, ct);

        // Example rows, in order: links to rows made before, dates relative to the workspace's today.
        var rows = 0;
        if (examples)
        {
            var today = (await _settings.CalendarAsync()).Today();
            var ids = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var example in recipe["examples"] as JsonArray ?? [])
            {
                var key = example!["table"]!.GetValue<string>();
                var values = new JsonObject();
                foreach (var (column, value) in example["values"]!.AsObject())
                    values[column] = Resolve(value, today, ids);
                var created = await CallAsync(HttpMethod.Post, $"{Api}/data/{Uri.EscapeDataString(names[key])}", credentials, values, ct);
                var index = counts.GetValueOrDefault(key);
                counts[key] = index + 1;
                ids[$"@ref:{key}:{index}"] = (created["data"] ?? created)["id"]?.DeepClone();
                rows++;
            }
        }

        // Dashboards and forms (shared and pinned for quick entry), then the note for the AI.
        var dashboards = new List<InstalledItem>();
        long? open = null;
        foreach (var dashboard in Fill(recipe["dashboards"] ?? new JsonArray(), names).AsArray())
        {
            var created = await CallAsync(HttpMethod.Post, $"{Api}/dashboards", credentials,
                new JsonObject { ["name"] = dashboard!["name"]!.DeepClone(), ["dashboard"] = dashboard["dashboard"]!.DeepClone() }, ct);
            var item = new InstalledItem(Id(created), dashboard["name"]!.GetValue<string>());
            dashboards.Add(item);
            // The one marked "open" (finance: Wealth), else the first.
            if (open is null || dashboard["open"]?.GetValue<bool>() == true)
                open = item.Id;
        }
        var forms = new List<InstalledItem>();
        foreach (var form in Fill(recipe["forms"] ?? new JsonArray(), names).AsArray())
        {
            var created = await CallAsync(HttpMethod.Post, $"{Api}/forms", credentials,
                new JsonObject { ["name"] = form!["name"]!.DeepClone(), ["form"] = form["form"]!.DeepClone(), ["isShared"] = true }, ct);
            forms.Add(new InstalledItem(Id(created), form["name"]!.GetValue<string>()));
        }
        await CallAsync(HttpMethod.Put, $"{Api}/templates/{Uri.EscapeDataString(id)}", credentials,
            new JsonObject { ["title"] = recipe["title"]!.DeepClone(), ["tables"] = JsonSerializer.SerializeToNode(names) }, ct);

        return new InstallResult(id, names, dashboards, forms, rows, open);
    }

    private JsonObject Require(string id) =>
        _catalog.Get(id) ?? throw new ApiNotFoundException($"There is no template '{id}'.", new Dictionary<string, object?> { ["template"] = id });

    /// <summary>The function a template's formulas call: used when it fits, made from its template when missing.</summary>
    private async Task EnsureFunctionAsync(JsonObject wanted, BentoSelfClient.Credentials credentials, CancellationToken ct)
    {
        var name = wanted["name"]!.GetValue<string>();
        var existing = await _api.SendAsync(HttpMethod.Get, $"{Api}/functions/{Uri.EscapeDataString(name)}", credentials, null, ct);
        if (existing.IsSuccess)
        {
            var fn = existing.Body is { } body && body.TryGetProperty("data", out var data) ? data : existing.Body!.Value;
            var mode = fn.TryGetProperty("mode", out var m) ? m.GetString() : "row";
            var arguments = fn.TryGetProperty("arguments", out var a) ? a.GetArrayLength() : 0;
            if (mode == wanted["mode"]?.GetValue<string>() && arguments == wanted["arguments"]?.GetValue<int>())
                return;
            throw new ApiConflictException($"A function named {name} exists already and does not fit the template; rename it under Functions first.");
        }
        var templates = await CallAsync(HttpMethod.Get, $"{Api}/functions/templates", credentials, null, ct);
        var template = ((templates["data"] ?? templates) as JsonArray ?? [])
            .FirstOrDefault(t => t?["id"]?.GetValue<string>() == wanted["template"]!.GetValue<string>())
            ?? throw new ApiUnprocessableEntityException($"The server has no function template '{wanted["template"]}'.");
        await CallAsync(HttpMethod.Post, $"{Api}/functions", credentials, new JsonObject
        {
            ["name"] = name,
            ["description"] = template["description"]?.DeepClone(),
            ["arguments"] = template["arguments"]?.DeepClone(),
            ["resultType"] = template["resultType"]?.DeepClone(),
            ["code"] = template["code"]?.DeepClone(),
            ["allowedHosts"] = new JsonArray(),
            ["credentials"] = new JsonArray(),
            ["mode"] = wanted["mode"]?.DeepClone() ?? "row"
        }, ct);
    }

    /// <summary>One call to the API as the user; its error, as it says it, otherwise.</summary>
    private async Task<JsonNode> CallAsync(HttpMethod method, string path, BentoSelfClient.Credentials credentials, JsonNode? body, CancellationToken ct)
    {
        var response = await _api.SendAsync(method, path, credentials, body is null ? null : JsonDocument.Parse(body.ToJsonString()).RootElement, ct);
        if (response.IsSuccess)
            return response.Body is { } json ? JsonNode.Parse(json.GetRawText())! : new JsonObject();
        var message = response.Body is { } error && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
            ? m.GetString()
            : response.RawBody;
        var text = $"{method} {path}: {message}";
        throw response.Status switch
        {
            401 or 403 => new ApiForbiddenException(text),
            404 => new ApiNotFoundException(text),
            409 => new ApiConflictException(text),
            _ => new ApiUnprocessableEntityException(text)
        };
    }

    private static long Id(JsonNode created) => (created["data"] ?? created)["id"]!.GetValue<long>();

    /// <summary>The recipe part with each {{key}} replaced by the table's name here.</summary>
    private static JsonNode Fill(JsonNode part, IReadOnlyDictionary<string, string> names)
    {
        var text = part.ToJsonString();
        foreach (var (key, name) in names)
            text = text.Replace("{{" + key + "}}", name, StringComparison.Ordinal);
        return JsonNode.Parse(text)!;
    }

    /// <summary>"@ref:projects:0" → the id that row got; "@days:-3", "@month:-2/15", "@monthEnd:-1" (with "T10:00:00") → a date from today.</summary>
    private static JsonNode? Resolve(JsonNode? value, DateOnly today, IReadOnlyDictionary<string, JsonNode?> ids)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || !text.StartsWith('@'))
            return value?.DeepClone();
        if (text.StartsWith("@ref:", StringComparison.Ordinal))
            return ids.TryGetValue(text, out var id) ? id?.DeepClone() : null;
        var time = text.IndexOf('T') is var t and > 0 ? text[t..] : "";
        var token = time.Length > 0 ? text[..text.IndexOf('T')] : text;
        DateOnly date;
        if (token.StartsWith("@days:", StringComparison.Ordinal))
            date = today.AddDays(int.Parse(token[6..], CultureInfo.InvariantCulture));
        else if (token.StartsWith("@monthEnd:", StringComparison.Ordinal))
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(int.Parse(token[10..], CultureInfo.InvariantCulture));
            date = first.AddMonths(1).AddDays(-1);
        }
        else if (token.StartsWith("@month:", StringComparison.Ordinal))
        {
            var parts = token[7..].Split('/');
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(int.Parse(parts[0], CultureInfo.InvariantCulture));
            date = first.AddDays(Math.Min(int.Parse(parts[1], CultureInfo.InvariantCulture), DateTime.DaysInMonth(first.Year, first.Month)) - 1);
        }
        else
            return value.DeepClone();
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + time;
    }

    /// <summary>"Transactions" with the prefix finance_ → "Finance transactions": tables of a second install are told apart.</summary>
    private static string PrefixLabel(string prefix, string label)
    {
        var words = prefix.Trim('_').Replace('_', ' ').Trim();
        return words.Length == 0 ? label : $"{char.ToUpperInvariant(words[0])}{words[1..]} {label.ToLowerInvariant()}";
    }

    private static string SuggestPrefix(JsonObject recipe, HashSet<string?> taken)
    {
        var bases = recipe["base"]!.AsObject().Select(p => p.Value!.GetValue<string>()).ToList();
        foreach (var prefix in (recipe["prefixes"] as JsonArray ?? []).Select(p => p!.GetValue<string>()).Append("my_"))
            if (bases.All(b => !taken.Contains((prefix + b).ToLowerInvariant())))
                return prefix;
        return "t2_";
    }

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });

    [GeneratedRegex("^[a-z][a-z0-9_]{0,20}$")]
    private static partial Regex Prefix();
}
