using Repere.Api.Db;
using Repere.Api.Db.Schema;
using Repere.Api.Exceptions;
using SqlKata.Execution;

namespace Repere.Api.Services;

/// <summary>
/// Describes a resolved include: which relation, which side, and nesting path.
/// </summary>
public sealed record ResolvedInclude
{
    public required ResolvedRelation Relation { get; init; }

    /// <summary>
    /// True when the include is from the "from" side of the relation (collection accessor).
    /// For 1:N from the parent side → array of children.
    /// For 1:N from the child side (toAccessor) → single parent object.
    /// </summary>
    public required bool IsFromSide { get; init; }

    /// <summary>The accessor name used in the include parameter.</summary>
    public required string AccessorName { get; init; }

    /// <summary>Nested includes that apply to the included table.</summary>
    public List<ResolvedInclude> Children { get; init; } = [];
}

public sealed record RelatedRowsResult
{
    public required IReadOnlyList<IDictionary<string, object?>> Rows { get; init; }
    public required int Page { get; init; }
    public required int PerPage { get; init; }
    public required long Total { get; init; }
    public required int TotalPages { get; init; }
}

/// <summary>
/// Resolves include parameter names to relation metadata and loads related rows in batches.
/// </summary>
public sealed class IncludeService
{
    private readonly QueryService _queryService;
    private readonly SchemaCache _schemaCache;

    public IncludeService(QueryService queryService, SchemaCache schemaCache)
    {
        _queryService = queryService;
        _schemaCache = schemaCache;
    }

    /// <summary>
    /// Resolve a flat list of include strings (possibly dot-notated) into a tree of ResolvedIncludes
    /// for a given table.
    /// </summary>
    public List<ResolvedInclude> ResolveIncludes(string tableName, IReadOnlyList<string> includeNames)
    {
        var model = _schemaCache.Current;
        var resolver = new RelationResolver();
        var resolved = resolver.Resolve(model);

        var tree = new List<ResolvedInclude>();

        foreach (var includeName in includeNames)
        {
            var parts = includeName.Split('.');
            BuildIncludeTree(tree, parts, 0, tableName, resolved.Relations);
        }

        return tree;
    }

    /// <summary>
    /// Load related rows for a batch of parent rows and embed them as nested properties.
    /// </summary>
    public async Task ApplyIncludesAsync(
        IReadOnlyList<IDictionary<string, object?>> rows,
        List<ResolvedInclude> includes)
    {
        if (rows.Count == 0 || includes.Count == 0)
            return;

        foreach (var include in includes)
        {
            await LoadIncludeAsync(rows, include);
        }
    }

    public static IReadOnlyList<string> GetRequiredLinkColumns(IReadOnlyList<ResolvedInclude> includes)
    {
        var requiredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var include in includes)
        {
            var def = include.Relation.Definition;
            if (def.Type == RelationType.OneToMany && !include.IsFromSide)
            {
                requiredColumns.Add(def.Column!);
            }
            else
            {
                requiredColumns.Add("id");
            }
        }

        return requiredColumns.ToList();
    }

    /// <summary>
    /// Load related rows for a single parent row via a relation accessor.
    /// Used by the sub-resource endpoint.
    /// </summary>
    public async Task<RelatedRowsResult> LoadRelatedRowsAsync(
        string tableName,
        long parentId,
        string relationAccessor,
        IQueryCollection query)
    {
        var include = ResolveInclude(tableName, relationAccessor)
            ?? throw new ApiNotFoundException(
                $"Unknown relation accessor '{relationAccessor}' on table '{tableName}'.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = tableName,
                    ["accessor"] = relationAccessor,
                    ["availableAccessors"] = GetAvailableAccessors(tableName, GetResolvedRelations())
                });

        var rel = include.Relation;
        var def = rel.Definition;
        var targetTableName = GetTargetTable(include);
        var targetTable = _schemaCache.GetTable(targetTableName)
            ?? throw new ApiNotFoundException(
                $"Table '{targetTableName}' does not exist.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["table"] = targetTableName
                });
        var parameters = QueryParameterParser.ParseRelated(query, targetTable);

        await using var scope = await _queryService.UseDataAsync();

        if (def.Type == RelationType.OneToMany)
        {
            if (include.IsFromSide)
            {
                // From parent → children (e.g., customers/:id/customer_orders → orders)
                var relatedQuery = scope.Db.Query(def.To).Where(def.Column!, parentId);
                return await ExecutePaginatedQuery(relatedQuery, parameters);
            }
            else
            {
                // From child → parent (e.g., orders/:id/customer → customers row)
                // First get the FK value from the child row
                object? childRow = await scope.Db.Query(tableName)
                    .Where("id", parentId)
                    .FirstOrDefaultAsync();

                if (childRow is null)
                    return EmptyRelatedRowsResult(parameters);

                var dict = ToDictionary(childRow);
                object? fkObj;
                var fkValue = dict.TryGetValue(def.Column!, out fkObj) ? fkObj : null;
                if (fkValue is null)
                    return EmptyRelatedRowsResult(parameters);

                var parentQuery = scope.Db.Query(def.From)
                    .Where("id", fkValue);

                return await ExecutePaginatedQuery(parentQuery, parameters);
            }
        }
        else // ManyToMany
        {
            var junctionTable = rel.JunctionTableName!;
            var junctionCols = rel.JunctionTable!.Columns;

            string localJunctionCol, remoteJunctionCol, remoteTable;
            if (include.IsFromSide)
            {
                // from side: find junction col referencing "from" table, remote col referencing "to" table
                localJunctionCol = junctionCols.First(c => string.Equals(c.References, def.From, StringComparison.OrdinalIgnoreCase)).Name;
                remoteJunctionCol = junctionCols.First(c => string.Equals(c.References, def.To, StringComparison.OrdinalIgnoreCase) && c.Name != localJunctionCol).Name;
                remoteTable = def.To;
            }
            else
            {
                localJunctionCol = junctionCols.First(c => string.Equals(c.References, def.To, StringComparison.OrdinalIgnoreCase)).Name;
                remoteJunctionCol = junctionCols.First(c => string.Equals(c.References, def.From, StringComparison.OrdinalIgnoreCase) && c.Name != localJunctionCol).Name;
                remoteTable = def.From;
            }

            var relatedQuery = scope.Db.Query(remoteTable)
                .Join(junctionTable, $"{junctionTable}.{remoteJunctionCol}", $"{remoteTable}.id")
                .Where($"{junctionTable}.{localJunctionCol}", parentId)
                .Select($"{remoteTable}.*");

            return await ExecutePaginatedQuery(relatedQuery, parameters);
        }
    }

    private async Task LoadIncludeAsync(
        IReadOnlyList<IDictionary<string, object?>> rows,
        ResolvedInclude include)
    {
        var rel = include.Relation;
        var def = rel.Definition;

        await using var scope = await _queryService.UseDataAsync();

        if (def.Type == RelationType.OneToMany)
        {
            if (include.IsFromSide)
            {
                // Parent rows → load children by FK
                var parentIds = rows
                    .Select(r => r.TryGetValue("id", out var v) ? v : null)
                    .Where(v => v is not null)
                    .Distinct()
                    .ToList();

                if (parentIds.Count == 0) return;

                var children = (await scope.Db.Query(def.To)
                    .WhereIn(def.Column!, parentIds)
                    .GetAsync())
                    .Select(ToDictionary)
                    .ToList();

                // Apply nested includes to children
                if (include.Children.Count > 0)
                    await ApplyIncludesAsync(children, include.Children);

                // Group children by FK value
                var grouped = children
                    .GroupBy(c => c.TryGetValue(def.Column!, out var v) ? v?.ToString() : null)
                    .ToDictionary(g => g.Key ?? "", g => g.ToList() as IReadOnlyList<IDictionary<string, object?>>);

                foreach (var row in rows)
                {
                    var id = row.TryGetValue("id", out var v) ? v?.ToString() : null;
                    row[include.AccessorName] = id is not null && grouped.TryGetValue(id, out var related) ? related : Array.Empty<IDictionary<string, object?>>();
                }
            }
            else
            {
                // Child rows → load parent by FK
                var fkValues = rows
                    .Select(r => r.TryGetValue(def.Column!, out var v) ? v : null)
                    .Where(v => v is not null)
                    .Distinct()
                    .ToList();

                if (fkValues.Count == 0) return;

                var parents = (await scope.Db.Query(def.From)
                    .WhereIn("id", fkValues)
                    .GetAsync())
                    .Select(ToDictionary)
                    .ToList();

                // Apply nested includes to parents
                if (include.Children.Count > 0)
                    await ApplyIncludesAsync(parents, include.Children);

                var parentLookup = parents.ToDictionary(
                    p => p.TryGetValue("id", out var v) ? v?.ToString() ?? "" : "",
                    p => p);

                foreach (var row in rows)
                {
                    var fk = row.TryGetValue(def.Column!, out var v) ? v?.ToString() : null;
                    row[include.AccessorName] = fk is not null && parentLookup.TryGetValue(fk, out var parent) ? parent : null;
                }
            }
        }
        else // ManyToMany
        {
            var junctionTable = rel.JunctionTableName!;
            var junctionCols = rel.JunctionTable!.Columns;

            string localJunctionCol, remoteJunctionCol, remoteTable;
            if (include.IsFromSide)
            {
                localJunctionCol = junctionCols.First(c => string.Equals(c.References, def.From, StringComparison.OrdinalIgnoreCase)).Name;
                remoteJunctionCol = junctionCols.First(c => string.Equals(c.References, def.To, StringComparison.OrdinalIgnoreCase) && c.Name != localJunctionCol).Name;
                remoteTable = def.To;
            }
            else
            {
                localJunctionCol = junctionCols.First(c => string.Equals(c.References, def.To, StringComparison.OrdinalIgnoreCase)).Name;
                remoteJunctionCol = junctionCols.First(c => string.Equals(c.References, def.From, StringComparison.OrdinalIgnoreCase) && c.Name != localJunctionCol).Name;
                remoteTable = def.From;
            }

            var parentIds = rows
                .Select(r => r.TryGetValue("id", out var v) ? v : null)
                .Where(v => v is not null)
                .Distinct()
                .ToList();

            if (parentIds.Count == 0) return;

            // Load junction + remote rows in one query
            var junctionRows = (await scope.Db.Query(junctionTable)
                .WhereIn(localJunctionCol, parentIds)
                .GetAsync())
                .Select(ToDictionary)
                .ToList();

            var remoteIds = junctionRows
                .Select(j => j.TryGetValue(remoteJunctionCol, out var v) ? v : null)
                .Where(v => v is not null)
                .Distinct()
                .ToList();

            var remoteRows = remoteIds.Count > 0
                ? (await scope.Db.Query(remoteTable)
                    .WhereIn("id", remoteIds)
                    .GetAsync())
                    .Select(ToDictionary)
                    .ToList()
                : [];

            // Apply nested includes to remote rows
            if (include.Children.Count > 0)
                await ApplyIncludesAsync(remoteRows, include.Children);

            var remoteLookup = remoteRows.ToDictionary(
                r => r.TryGetValue("id", out var v) ? v?.ToString() ?? "" : "",
                r => r);

            // Build mapping: parentId → list of remote rows
            var mapping = new Dictionary<string, List<IDictionary<string, object?>>>();
            foreach (var jr in junctionRows)
            {
                var localId = jr.TryGetValue(localJunctionCol, out var lv) ? lv?.ToString() : null;
                var remoteId = jr.TryGetValue(remoteJunctionCol, out var rv) ? rv?.ToString() : null;

                if (localId is null || remoteId is null) continue;
                if (!remoteLookup.TryGetValue(remoteId, out var remoteRow)) continue;

                if (!mapping.TryGetValue(localId, out var list))
                {
                    list = [];
                    mapping[localId] = list;
                }
                list.Add(remoteRow);
            }

            foreach (var row in rows)
            {
                var id = row.TryGetValue("id", out var v) ? v?.ToString() : null;
                row[include.AccessorName] = id is not null && mapping.TryGetValue(id, out var related)
                    ? related
                    : Array.Empty<IDictionary<string, object?>>();
            }
        }
    }

    private void BuildIncludeTree(
        List<ResolvedInclude> tree,
        string[] parts,
        int depth,
        string tableName,
        IReadOnlyList<ResolvedRelation> allRelations)
    {
        var accessorName = parts[depth];

        // Check if this accessor is already in the tree at this level
        var existing = tree.FirstOrDefault(
            i => string.Equals(i.AccessorName, accessorName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var include = FindInclude(tableName, accessorName, allRelations)
                ?? throw new ApiValidationException(
                    $"Unknown include accessor '{accessorName}' on table '{tableName}'.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["table"] = tableName,
                        ["accessor"] = accessorName,
                        ["availableAccessors"] = GetAvailableAccessors(tableName, allRelations)
                    });

            tree.Add(include);
            existing = include;
        }

        // If there are more parts, recurse into the target table
        if (depth + 1 < parts.Length)
        {
            var targetTable = GetTargetTable(existing);
            BuildIncludeTree(existing.Children, parts, depth + 1, targetTable, allRelations);
        }
    }

    private static string GetTargetTable(ResolvedInclude include)
    {
        var def = include.Relation.Definition;
        if (def.Type == RelationType.OneToMany)
            return include.IsFromSide ? def.To : def.From;

        // N:M
        return include.IsFromSide ? def.To : def.From;
    }

    private static ResolvedInclude? FindInclude(
        string tableName,
        string accessorName,
        IReadOnlyList<ResolvedRelation> relations)
    {
        foreach (var rel in relations)
        {
            var def = rel.Definition;

            // Check fromAccessor (table must be the "from" table)
            if (string.Equals(def.From, tableName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rel.FromAccessor, accessorName, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedInclude
                {
                    Relation = rel,
                    IsFromSide = true,
                    AccessorName = rel.FromAccessor
                };
            }

            // Check toAccessor (table must be the "to" table)
            if (string.Equals(def.To, tableName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rel.ToAccessor, accessorName, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedInclude
                {
                    Relation = rel,
                    IsFromSide = false,
                    AccessorName = rel.ToAccessor
                };
            }
        }

        return null;
    }

    private ResolvedInclude? ResolveInclude(string tableName, string accessorName)
    {
        return FindInclude(tableName, accessorName, GetResolvedRelations());
    }

    private IReadOnlyList<ResolvedRelation> GetResolvedRelations()
    {
        var model = _schemaCache.Current;
        var resolver = new RelationResolver();
        return resolver.Resolve(model).Relations;
    }

    private static RelatedRowsResult EmptyRelatedRowsResult(ListQueryParameters parameters)
    {
        return new RelatedRowsResult
        {
            Rows = [],
            Page = parameters.Page,
            PerPage = parameters.PerPage,
            Total = 0,
            TotalPages = 0
        };
    }

    private static List<string> GetAvailableAccessors(
        string tableName,
        IReadOnlyList<ResolvedRelation> relations)
    {
        var accessors = new List<string>();
        foreach (var rel in relations)
        {
            var def = rel.Definition;
            if (string.Equals(def.From, tableName, StringComparison.OrdinalIgnoreCase))
                accessors.Add(rel.FromAccessor);
            if (string.Equals(def.To, tableName, StringComparison.OrdinalIgnoreCase))
                accessors.Add(rel.ToAccessor);
        }
        return accessors;
    }

    private static async Task<RelatedRowsResult> ExecutePaginatedQuery(
        SqlKata.Query query,
        ListQueryParameters parameters)
    {
        // Apply filters
        CrudService.ApplyFiltersStatic(query, parameters.Filters);

        // Count
        var total = await query.Clone().CountAsync<long>();

        // Sort
        foreach (var sort in parameters.Sorts)
        {
            if (sort.Descending)
                query = query.OrderByDesc(sort.Column);
            else
                query = query.OrderBy(sort.Column);
        }

        // Paginate
        query = query.ForPage(parameters.Page, parameters.PerPage);

        var rows = (await query.GetAsync()).Select(ToDictionary).ToList();
        var totalPages = parameters.PerPage > 0
            ? (int)Math.Ceiling((double)total / parameters.PerPage)
            : 0;

        return new RelatedRowsResult
        {
            Rows = rows,
            Page = parameters.Page,
            PerPage = parameters.PerPage,
            Total = total,
            TotalPages = totalPages
        };
    }

    private static IDictionary<string, object?> ToDictionary(object row)
    {
        if (row is IDictionary<string, object?> dict)
            return dict;

        if (row is IDictionary<string, object> nonNullableDict)
        {
            var result = new Dictionary<string, object?>(nonNullableDict.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in nonNullableDict)
            {
                result[kvp.Key] = kvp.Value;
            }
            return result;
        }

        throw new InvalidOperationException($"Unexpected row type: {row.GetType().FullName}");
    }
}
