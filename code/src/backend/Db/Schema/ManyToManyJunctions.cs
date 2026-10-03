namespace Repere.Api.Db.Schema;

/// <summary>
/// Keeps every N:M junction table visible in the model.
///
/// An N:M relation without <c>junctionTable</c> still gets a real table in the
/// database (<c>_jt_left_right</c>), but nothing in the app could see or edit
/// it. The model the app reads therefore always lists that table and points
/// the relation at it. The table already exists with exactly these columns,
/// so adopting it into the model changes no DDL.
/// </summary>
public static class ManyToManyJunctions
{
    private const string CreatedAtColumn = "created_at";
    private const string CreatedAtDefaultSql = "CURRENT_TIMESTAMP";

    private static readonly RelationResolver Resolver = new();

    /// <summary>
    /// A stored (applied) model: every implicit junction exists in the
    /// database, so each one is adopted.
    /// </summary>
    public static ModelJson MakeExplicit(ModelJson model) =>
        Adopt(model, relation => Resolver.BuildJunctionTable(relation));

    /// <summary>
    /// A requested model (designer draft, imported file, startup model) and
    /// the current model it is diffed against. Every N:M relation in the
    /// request gets its junction as a table: an existing junction is kept, a
    /// new one is added as a new table.
    ///
    /// One exception: a request that leaves the junction implicit but names
    /// its key columns differently from the existing table. Key columns are
    /// primary key columns, which cannot be renamed, so that junction is
    /// handed back to the implicit "rebuild the junction" path on both sides
    /// (destructive, confirmed by the user) and adopted again on the next read.
    /// </summary>
    public static (ModelJson Current, ModelJson Requested) ForDiff(ModelJson current, ModelJson requested)
    {
        var currentTables = current.Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adopted = Adopt(requested, relation =>
        {
            var built = Resolver.BuildJunctionTable(relation);
            if (!currentTables.TryGetValue(built.Name, out var existing))
                return built;
            if (KeyColumnsMatch(existing, built))
                return existing;
            rebuilt.Add(built.Name);
            return null;
        });

        if (rebuilt.Count == 0)
            return (current, adopted);

        var releasedCurrent = current with
        {
            Tables = current.Tables.Where(t => !rebuilt.Contains(t.Name)).ToList(),
            Relations = current.Relations
                .Select(r => r.Type == RelationType.ManyToMany && r.JunctionTable is not null && rebuilt.Contains(r.JunctionTable)
                    ? r with { JunctionTable = null }
                    : r)
                .ToList()
        };
        return (releasedCurrent, adopted);
    }

    private static bool KeyColumnsMatch(TableDef existing, TableDef built)
    {
        var existingKeys = existing.Columns
            .Where(c => !string.IsNullOrWhiteSpace(c.References))
            .Select(c => $"{c.Name.ToLowerInvariant()}>{c.References!.ToLowerInvariant()}")
            .ToHashSet();
        var builtKeys = built.Columns
            .Take(2)
            .Select(c => $"{c.Name.ToLowerInvariant()}>{c.References!.ToLowerInvariant()}")
            .ToHashSet();
        return existingKeys.SetEquals(builtKeys);
    }

    private static ModelJson Adopt(ModelJson model, Func<RelationDef, TableDef?> junctionFor)
    {
        var tables = model.Tables.ToList();
        var relations = model.Relations.ToList();
        var changed = false;

        for (var index = 0; index < relations.Count; index++)
        {
            var relation = relations[index];
            if (relation.Type != RelationType.ManyToMany || !string.IsNullOrWhiteSpace(relation.JunctionTable))
                continue;

            var junctionName = Resolver.GetJunctionTableName(relation);
            var inModel = tables.Any(t => string.Equals(t.Name, junctionName, StringComparison.OrdinalIgnoreCase));
            if (!inModel)
            {
                var junction = junctionFor(relation);
                if (junction is null)
                    continue;
                tables.Add(junction);
            }

            relations[index] = WithJunctionColumns(relation) with { JunctionTable = junctionName };
            changed = true;
        }

        for (var index = 0; index < tables.Count; index++)
        {
            var withDefault = WithCreatedAtDefault(tables[index], relations);
            if (!ReferenceEquals(withDefault, tables[index]))
            {
                tables[index] = withDefault;
                changed = true;
            }
        }

        return changed ? model with { Tables = tables, Relations = relations } : model;
    }

    /// <summary>Names the junction columns on the relation (as an explicit junction relation does).</summary>
    private static RelationDef WithJunctionColumns(RelationDef relation)
    {
        var columns = Resolver.BuildJunctionTable(relation).Columns;
        var isSelfReferential = string.Equals(relation.From, relation.To, StringComparison.OrdinalIgnoreCase);

        string fromColumn;
        string toColumn;
        if (isSelfReferential)
        {
            fromColumn = columns[0].Name;
            toColumn = columns[1].Name;
        }
        else
        {
            fromColumn = columns.First(c => string.Equals(c.References, relation.From, StringComparison.OrdinalIgnoreCase)).Name;
            toColumn = columns.First(c => string.Equals(c.References, relation.To, StringComparison.OrdinalIgnoreCase)).Name;
        }

        return relation with
        {
            FromColumn = relation.FromColumn ?? fromColumn,
            ToColumn = relation.ToColumn ?? toColumn
        };
    }

    /// <summary>
    /// The junction's <c>created_at</c> is filled by the database. The SQL
    /// default is internal (not part of Model JSON), so it is restored here
    /// for adopted junction tables; otherwise inserts would demand a value.
    /// </summary>
    private static TableDef WithCreatedAtDefault(TableDef table, IReadOnlyList<RelationDef> relations)
    {
        var isJunction = relations.Any(relation =>
            relation.Type == RelationType.ManyToMany &&
            string.Equals(relation.JunctionTable, table.Name, StringComparison.OrdinalIgnoreCase));
        if (!isJunction)
            return table;

        var columnIndex = table.Columns.FindIndex(column =>
            string.Equals(column.Name, CreatedAtColumn, StringComparison.OrdinalIgnoreCase) &&
            column.Type == ColumnType.DateTime &&
            !column.Nullable &&
            column.DefaultValue is null &&
            column.InternalDefaultSql is null);
        if (columnIndex < 0)
            return table;

        var columns = table.Columns.ToList();
        columns[columnIndex] = columns[columnIndex] with { InternalDefaultSql = CreatedAtDefaultSql };
        return table with { Columns = columns };
    }
}
