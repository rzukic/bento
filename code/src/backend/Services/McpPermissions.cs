namespace Bento.Api.Services;

/// <summary>
/// What an app connected over OAuth may do, area by area (Step 4.6). The user picks
/// them on the consent page; the user's role caps them; every API call made with
/// the app's token is checked against them (<see cref="RequiredFor"/>), so a grant
/// without <c>schema.write</c> cannot change the schema even for an admin.
/// </summary>
public static class McpPermissions
{
    /// <summary>Tables, rows, the schema, dashboards, forms, saved queries, functions: read, and run analyses.</summary>
    public const string Read = "data.read";
    public const string WriteRows = "data.write";
    public const string DeleteRows = "data.delete";
    /// <summary>Dashboards and saved queries.</summary>
    public const string Dashboards = "dashboards.write";
    public const string Forms = "forms.write";
    /// <summary>Tables, columns and relations: can delete data.</summary>
    public const string Schema = "schema.write";
    /// <summary>JavaScript functions and price sources.</summary>
    public const string Functions = "functions.write";

    public static readonly string[] All = [Read, WriteRows, DeleteRows, Dashboards, Forms, Schema, Functions];

    /// <summary>
    /// The most a role allows, matching the REST API's own rules: everyone reads and
    /// keeps dashboards and saved queries; editors also write rows and forms; admins
    /// also change the schema and functions.
    /// </summary>
    public static IReadOnlyList<string> ForRole(string? role) => role switch
    {
        "admin" => All,
        "editor" => [Read, WriteRows, DeleteRows, Dashboards, Forms],
        "viewer" => [Read, Dashboards],
        _ => [Read]
    };

    /// <summary>
    /// What an API key may do through MCP: read keys read; read-write keys act with the
    /// API role (rows, imports, dashboards, forms), never schema or functions.
    /// </summary>
    public static IReadOnlyList<string> ForApiKeyScope(string scope) =>
        scope == ApiKeyService.ReadWriteScope ? [Read, WriteRows, DeleteRows, Dashboards, Forms] : [Read];

    /// <summary>Known permissions only, always with read, in the canonical order.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? permissions)
    {
        var set = new HashSet<string>(permissions ?? [], StringComparer.Ordinal) { Read };
        return All.Where(set.Contains).ToList();
    }

    /// <summary>
    /// OAuth scope text to permissions: the ids themselves, plus the short forms
    /// "read" and "write" (= add and change rows) that generic clients send.
    /// </summary>
    public static IReadOnlyList<string> FromScope(string? scope)
    {
        var parts = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var permissions = new List<string>();
        foreach (var part in parts)
        {
            if (part.Equals("write", StringComparison.OrdinalIgnoreCase)) permissions.Add(WriteRows);
            else if (All.Contains(part, StringComparer.Ordinal)) permissions.Add(part);
        }
        return Normalize(permissions);
    }

    public static IReadOnlyList<string> Cap(IEnumerable<string> permissions, string? role)
    {
        var allowed = ForRole(role);
        return Normalize(permissions).Where(allowed.Contains).ToList();
    }

    /// <summary>
    /// The permission an API request needs when made with an app's token, or null
    /// when apps may not call it at all (users, keys, credentials, connections,
    /// sign-in). Everything not listed here is refused.
    /// </summary>
    public static string? RequiredFor(string method, string path)
    {
        var p = path.TrimEnd('/').ToLowerInvariant();
        const string api = "/" + ApiRoutes.Prefix;
        bool Under(string area) => p == api + area || p.StartsWith(api + area + "/", StringComparison.Ordinal);

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return Under("/data") || Under("/query") || Under("/queries") || Under("/dashboards") || Under("/forms") ||
                   p == api + "/admin/schema" || p == api + "/admin/schema/model" || Under("/formulas") ||
                   Under("/functions") || Under("/price-sources") || p == api + "/openapi.json" || Under("/views") || p == api + "/workspaces" ||
                   Under("/templates") || Under("/query-tables") ||
                   (Under("/import") && p.EndsWith("/profiles", StringComparison.Ordinal))
                ? Read
                : null;
        }

        // Reads that are POSTs: running queries, checking formulas, previewing a schema.
        if (HttpMethods.IsPost(method) &&
            (p == api + "/query" || p == api + "/query/variables" || (Under("/queries") && p.EndsWith("/run", StringComparison.Ordinal)) ||
             p == api + "/formulas/validate" || p == api + "/formulas/preview" || p == api + "/admin/schema/preview" || p == api + "/admin/schema/patch/preview" ||
             p == api + "/price-sources/refresh" || (Under("/query-tables") && p.EndsWith("/refresh", StringComparison.Ordinal))))
            return Read;

        if (Under("/data"))
            return HttpMethods.IsDelete(method) ? DeleteRows : WriteRows;
        if (Under("/import"))
            return WriteRows;
        // A saved query as a table changes the model.
        if (Under("/queries") && p.EndsWith("/table", StringComparison.Ordinal))
            return Schema;
        if (Under("/dashboards") || Under("/queries"))
            return Dashboards;
        if (Under("/forms"))
            return Forms;
        // Which templates the workspace has is part of its model.
        if (p == api + "/admin/schema" || p == api + "/admin/migrations/rollback" || Under("/templates"))
            return Schema;
        if (Under("/functions") || Under("/price-sources"))
            return Functions;
        return null;
    }

    public static string ToScope(IEnumerable<string> permissions) => string.Join(' ', Normalize(permissions));
}
