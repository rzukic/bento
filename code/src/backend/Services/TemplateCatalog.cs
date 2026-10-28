using System.Text.Json.Nodes;

namespace Bento.Api.Services;

/// <summary>
/// The templates Bento offers (Step 5.15): recipes in <c>Templates/*.json</c> —
/// tables and their links, dashboards, forms, example rows, functions they
/// need — with <c>{{key}}</c> for each table's name, so a prefix can keep them
/// apart from existing tables. Installed by <see cref="TemplateInstaller"/>,
/// the same way for the app and for MCP clients.
/// </summary>
public sealed class TemplateCatalog
{
    private readonly Dictionary<string, JsonObject> _recipes;
    private readonly List<string> _order = ["projects-hours", "habits-health", "appointments", "finance"];

    public TemplateCatalog()
    {
        var assembly = typeof(TemplateCatalog).Assembly;
        _recipes = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Bento.Api.Templates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                return JsonNode.Parse(stream)!.AsObject();
            })
            .ToDictionary(r => r["id"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    /// <summary>What the gallery shows: id, title, description, the table names and the prefixes to try.</summary>
    public sealed record Summary(string Id, string Title, string Description, Dictionary<string, string> Base, IReadOnlyList<string> Prefixes,
        IReadOnlyList<string> Dashboards, IReadOnlyList<string> Forms, int ExampleRows);

    public IReadOnlyList<Summary> List() =>
        _recipes.Values
            .OrderBy(r => _order.IndexOf(r["id"]!.GetValue<string>()) is var i and >= 0 ? i : int.MaxValue)
            .Select(r => new Summary(
                r["id"]!.GetValue<string>(),
                r["title"]!.GetValue<string>(),
                r["description"]!.GetValue<string>(),
                r["base"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()),
                (r["prefixes"] as JsonArray ?? []).Select(p => p!.GetValue<string>()).ToList(),
                (r["dashboards"] as JsonArray ?? []).Select(d => d!["name"]!.GetValue<string>()).ToList(),
                (r["forms"] as JsonArray ?? []).Select(f => f!["name"]!.GetValue<string>()).ToList(),
                (r["examples"] as JsonArray)?.Count ?? 0))
            .ToList();

    /// <summary>A copy of the recipe, or null.</summary>
    public JsonObject? Get(string id) => _recipes.TryGetValue(id, out var recipe) ? recipe.DeepClone().AsObject() : null;
}
