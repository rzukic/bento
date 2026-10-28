using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bento.Api.Services;

/// <summary>
/// Dashboard JSON from before Bento's widgets stopped assuming money (2026-10):
/// a <c>budget</c> widget (spending per category against a monthly limit)
/// becomes a <c>target</c> widget with the same numbers, and a Sankey that drew
/// income → expenses (<c>cashflow</c>, also the old default for one level)
/// becomes <c>balance</c> with those labels. Applied to everything stored once
/// (bootstrap) and to whatever is saved or restored, so old states and old
/// clients keep working.
/// </summary>
public static class DashboardUpgrade
{
    /// <summary>The labels the money-flow Sankey drew before they could be chosen.</summary>
    public static readonly JsonObject MoneyLabels = new() { ["center"] = "Income", ["rest"] = "Saved", ["shortfall"] = "From savings" };

    /// <summary>Whether the JSON may hold something to upgrade (a cheap look before parsing).</summary>
    public static bool MayNeed(string json) => json.Contains("\"budget\"", StringComparison.Ordinal) || json.Contains("sankey", StringComparison.Ordinal);

    public static string Apply(string json)
    {
        if (!MayNeed(json) || JsonNode.Parse(json) is not JsonObject document)
            return json;
        return Apply(document) ? document.ToJsonString() : json;
    }

    public static JsonElement Apply(JsonElement dashboard)
    {
        var json = dashboard.GetRawText();
        var upgraded = Apply(json);
        return ReferenceEquals(upgraded, json) ? dashboard : JsonDocument.Parse(upgraded).RootElement.Clone();
    }

    /// <summary>Upgrades the widgets in place; true when anything changed.</summary>
    public static bool Apply(JsonObject document)
    {
        var changed = false;
        foreach (var widget in (document["widgets"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = widget["type"]?.GetValueKind() == JsonValueKind.String ? widget["type"]!.GetValue<string>() : null;
            if (type == "budget")
            {
                widget["type"] = "target";
                if (widget["data"] is JsonObject data && data["budget"] is JsonObject budget)
                {
                    data.Remove("budget");
                    data["target"] = Target(budget);
                }
                changed = true;
            }
            else if (type == "sankey")
            {
                var display = widget["display"] as JsonObject;
                var mode = display?["sankey"]?.GetValueKind() == JsonValueKind.String ? display["sankey"]!.GetValue<string>() : null;
                var groups = (widget["data"]?["query"]?["groupBy"] as JsonArray)?.Count ?? 0;
                // One level without a mode drew income → expenses; it keeps doing so.
                if (mode == "cashflow" || (mode is null && groups == 1))
                {
                    display ??= new JsonObject();
                    widget["display"] = display;
                    display["sankey"] = "balance";
                    if (display["sankeyLabels"] is null)
                        display["sankeyLabels"] = MoneyLabels.DeepClone();
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>A budget's spending (by sign) per category against its monthly limit, as a target.</summary>
    private static JsonObject Target(JsonObject budget)
    {
        string Text(string name) => budget[name]?.GetValueKind() == JsonValueKind.String ? budget[name]!.GetValue<string>() : "";
        var amount = Text("amount").Replace("\"", "");
        var positive = Text("expenses") == "positive";
        var target = new JsonObject
        {
            ["source"] = Text("source"),
            ["value"] = positive ? $"SUM({amount} WHERE {amount} > 0)" : $"-SUM({amount} WHERE {amount} < 0)",
            ["date"] = Text("date"),
            ["group"] = Text("category"),
            ["target"] = Text("limit"),
            ["per"] = "month",
            ["period"] = Text("period") == "range" ? "range" : "current",
            ["kind"] = "limit"
        };
        return target;
    }
}
