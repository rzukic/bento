using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bento.Api.Db;
using Bento.Api.Exceptions;
using Bento.Api.Services.Workspaces;
using Dapper;

namespace Bento.Api.Services;

/// <summary>
/// The templates installed in a workspace (finance, projects & hours …) and
/// which tables they made. Bento itself knows no use case: what the AI and
/// MCP clients are told about one — a template's conventions, its tables —
/// comes from here, and only for the workspaces that have it.
/// </summary>
public sealed partial class TemplateRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ISystemPool _systemPool;
    private readonly SchemaCache _schemaCache;

    public TemplateRegistry(ISystemPool systemPool, SchemaCache schemaCache)
    {
        _systemPool = systemPool;
        _schemaCache = schemaCache;
    }

    /// <param name="Tables">The template's tables by their name in the template → their name here (a prefix when a name was taken).</param>
    public sealed record Installed(string Id, string Title, string InstalledAt, Dictionary<string, string> Tables);

    private string Table => _systemPool.Provider == DatabaseProvider.Postgres ? "_system.workspace_settings" : "workspace_settings";

    /// <summary>"templates" for the default workspace, "templates@3" for workspace 3 (gone with it).</summary>
    private static string Key => WorkspaceContext.Current.IsDefault ? "templates" : "templates@" + WorkspaceContext.Id.ToString(CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<Installed>> ListAsync()
    {
        string? json;
        using (var conn = await _systemPool.GetConnectionAsync())
            json = await conn.ExecuteScalarAsync<string?>($"SELECT value FROM {Table} WHERE key = @Key", new { Key });
        if (json is null)
        {
            // Never noted: a finance setup from before templates were noted is found by its tables, and noted.
            if (DetectFinance() is not { } tables)
                return [];
            var found = new List<Installed> { new("finance", "Finance", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), tables) };
            await SaveAsync(found);
            return found;
        }
        try
        {
            return JsonSerializer.Deserialize<List<Installed>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Notes that a template was installed here with these tables. Installed
    /// again under other names (a prefix), both are kept; a note whose tables
    /// are all gone is dropped.
    /// </summary>
    public async Task<Installed> RecordAsync(string id, string? title, Dictionary<string, string>? tables)
    {
        if (!TemplateId().IsMatch(id))
            throw Invalid("id", "A template id is lower-case letters, digits and dashes.");
        var mapping = (tables ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Key) && !string.IsNullOrWhiteSpace(t.Value))
            .ToDictionary(t => t.Key.Trim(), t => t.Value.Trim());
        var missing = mapping.Values.FirstOrDefault(t => _schemaCache.GetTable(t) is null);
        if (missing is not null)
            throw Invalid("tables", $"There is no table '{missing}'.");
        var installed = new Installed(id, string.IsNullOrWhiteSpace(title) ? id : title.Trim(), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), mapping);
        var list = (await ListAsync())
            .Where(t => !(t.Id == id && (SameTables(t.Tables, mapping) || t.Tables.Values.All(table => _schemaCache.GetTable(table) is null))))
            .Append(installed).ToList();
        await SaveAsync(list);
        return installed;
    }

    /// <summary>Forgets every install of the template (its tables stay).</summary>
    public async Task RemoveAsync(string id) => await SaveAsync((await ListAsync()).Where(t => t.Id != id).ToList());

    public async Task<bool> HasAsync(string id) => (await ListAsync()).Any(t => t.Id == id && t.Tables.Values.Any(table => _schemaCache.GetTable(table) is not null));

    /// <summary>
    /// What the AI and MCP clients are told about this workspace's templates:
    /// which tables a template made (by their names here) and where its
    /// conventions are written. Null when it has none.
    /// </summary>
    public async Task<string?> ContextAsync()
    {
        var installed = await ListAsync();
        if (installed.Count == 0)
            return null;
        var text = new StringBuilder();
        foreach (var template in installed)
        {
            var tables = template.Tables.Where(t => _schemaCache.GetTable(t.Value) is not null).ToList();
            if (tables.Count == 0)
                continue;
            text.Append($"- The {template.Title} template is installed: ");
            text.Append(string.Join(", ", tables.Select(t => t.Key == t.Value ? $"`{t.Value}`" : $"`{t.Value}` (its {t.Key})")));
            text.Append('.');
            if (template.Id == "finance")
                text.Append(" Its conventions (money out is negative, accounts, categories, trades, prices): bento_guide topic finance.");
            text.AppendLine();
        }
        return text.Length == 0 ? null : "Templates in this workspace:\n" + text.ToString().TrimEnd();
    }

    /// <summary>Kept even when empty: an empty list is "none installed", no value at all is "never noted".</summary>
    private async Task SaveAsync(IReadOnlyList<Installed> list)
    {
        using var conn = await _systemPool.GetConnectionAsync();
        await conn.ExecuteAsync($"DELETE FROM {Table} WHERE key = @Key", new { Key });
        await conn.ExecuteAsync($"INSERT INTO {Table} (key, value) VALUES (@Key, @Value)", new { Key, Value = JsonSerializer.Serialize(list, Json) });
    }

    private static readonly string[] FinanceTables = ["accounts", "categories", "transactions", "trades", "prices", "dividends"];

    /// <summary>The finance template's six tables under one prefix ("", "finance_" …), with its key columns; null when there are none.</summary>
    private Dictionary<string, string>? DetectFinance()
    {
        foreach (var accounts in _schemaCache.Current.Tables.Where(t => t.Name.EndsWith("accounts", StringComparison.OrdinalIgnoreCase)))
        {
            var prefix = accounts.Name[..^"accounts".Length];
            var tables = FinanceTables.ToDictionary(t => t, t => _schemaCache.GetTable(prefix + t));
            if (tables.Values.Any(t => t is null))
                continue;
            bool Has(string table, params string[] columns) =>
                columns.All(c => tables[table]!.Columns.Any(column => string.Equals(column.Name, c, StringComparison.OrdinalIgnoreCase)));
            if (Has("transactions", "amount", "account_id", "category_id") && Has("trades", "isin", "shares") && Has("prices", "isin", "price"))
                return tables.ToDictionary(t => t.Key, t => t.Value!.Name);
        }
        return null;
    }

    private static bool SameTables(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(t => b.TryGetValue(t.Key, out var other) && string.Equals(other, t.Value, StringComparison.OrdinalIgnoreCase));

    private static ApiValidationException Invalid(string field, string message) =>
        new(message, new Dictionary<string, object?> { ["field"] = field });

    [GeneratedRegex("^[a-z][a-z0-9-]{1,40}$")]
    private static partial Regex TemplateId();
}
