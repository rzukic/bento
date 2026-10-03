using System.Collections.Concurrent;
using Bento.Api.Db;
using Bento.Api.Db.Schema;

namespace Bento.Api.Formula;

public enum NavigationKind
{
    /// <summary>A reference column of this table: one row (<c>category_id.name</c>).</summary>
    Reference,
    /// <summary>A 1:N relation from this table: the rows whose reference column points here.</summary>
    Children,
    /// <summary>An N:M relation: the rows linked through the junction table.</summary>
    Junction
}

/// <summary>
/// A link a formula can follow from a table with a dot. Reference links
/// give one value (<c>category_id.name</c>); the others give several rows,
/// which formulas reach through SUM, COUNT, AVG, MIN or MAX.
/// </summary>
/// <param name="Names">What a formula may write: accessor, reference column or table name.</param>
/// <param name="ForeignKey">Reference: this table's column; Children: the child table's column.</param>
/// <param name="JunctionThis">Junction: the junction column pointing at this table.</param>
/// <param name="JunctionTarget">Junction: the junction column pointing at the target.</param>
public sealed record FormulaNavigation(
    string Name,
    IReadOnlyList<string> Names,
    NavigationKind Kind,
    string Target,
    string? ForeignKey = null,
    string? Junction = null,
    string? JunctionThis = null,
    string? JunctionTarget = null)
{
    public bool ToMany => Kind != NavigationKind.Reference;
}

/// <summary>The tables and links formulas can use: the applied model or a draft.</summary>
public sealed class FormulaSchema
{
    private static readonly RelationResolver Resolver = new();

    private readonly Dictionary<string, TableDef> _tables;
    private readonly IReadOnlyList<RelationDef> _relations;
    private readonly ConcurrentDictionary<string, IReadOnlyList<FormulaNavigation>> _navigations = new(StringComparer.OrdinalIgnoreCase);

    public FormulaSchema(IEnumerable<TableDef> tables, IEnumerable<RelationDef>? relations = null)
    {
        _tables = new Dictionary<string, TableDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
            _tables.TryAdd(table.Name, table);
        _relations = relations?.ToList() ?? [];
    }

    public static FormulaSchema From(ModelJson model) => new(model.Tables, model.Relations);

    /// <summary>The model with one table replaced (a table being edited in the designer).</summary>
    public static FormulaSchema WithTable(ModelJson model, TableDef table) =>
        new(model.Tables.Where(t => !string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase)).Append(table), model.Relations);

    public TableDef? Table(string name) => _tables.GetValueOrDefault(name);

    public FormulaNavigation? FindNavigation(TableDef table, string name) =>
        Navigations(table).FirstOrDefault(n => n.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<FormulaNavigation> Navigations(TableDef table) =>
        _navigations.GetOrAdd(table.Name, _ => Build(table));

    private IReadOnlyList<FormulaNavigation> Build(TableDef table)
    {
        var navigations = new List<FormulaNavigation>();

        foreach (var column in table.Columns.Where(c => c.Type == ColumnType.Reference && !string.IsNullOrWhiteSpace(c.References)))
        {
            if (Table(column.References!) is null)
                continue;
            var relation = _relations.FirstOrDefault(r =>
                r.Type == RelationType.OneToMany &&
                string.Equals(r.To, table.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Column, column.Name, StringComparison.OrdinalIgnoreCase));
            var names = new List<string> { column.Name };
            if (relation is not null)
                names.Add(RelationResolver.ResolveToAccessor(relation));
            navigations.Add(new FormulaNavigation(names[^1], names, NavigationKind.Reference, column.References!, ForeignKey: column.Name));
        }

        foreach (var relation in _relations)
        {
            if (relation.Type == RelationType.OneToMany &&
                string.Equals(relation.From, table.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(relation.Column) && Table(relation.To) is not null)
            {
                var accessor = RelationResolver.ResolveFromAccessor(relation);
                navigations.Add(new FormulaNavigation(accessor, [accessor, relation.To], NavigationKind.Children, relation.To, ForeignKey: relation.Column));
            }

            if (relation.Type != RelationType.ManyToMany)
                continue;
            var junction = relation.JunctionTable ?? Resolver.GetJunctionTableName(relation);
            var built = Resolver.BuildJunctionTable(relation);
            var fromColumn = relation.FromColumn ?? built.Columns.First(c => string.Equals(c.References, relation.From, StringComparison.OrdinalIgnoreCase)).Name;
            var toColumn = relation.ToColumn ?? built.Columns.Last(c => string.Equals(c.References, relation.To, StringComparison.OrdinalIgnoreCase)).Name;

            if (string.Equals(relation.From, table.Name, StringComparison.OrdinalIgnoreCase) && Table(relation.To) is not null)
            {
                var accessor = RelationResolver.ResolveFromAccessor(relation);
                navigations.Add(new FormulaNavigation(accessor, [accessor, relation.To], NavigationKind.Junction, relation.To,
                    Junction: junction, JunctionThis: fromColumn, JunctionTarget: toColumn));
            }
            if (string.Equals(relation.To, table.Name, StringComparison.OrdinalIgnoreCase) && Table(relation.From) is not null)
            {
                var accessor = RelationResolver.ResolveToAccessor(relation);
                navigations.Add(new FormulaNavigation(accessor, [accessor, relation.From], NavigationKind.Junction, relation.From,
                    Junction: junction, JunctionThis: toColumn, JunctionTarget: fromColumn));
            }
        }

        // A table name stands for a link only when exactly one link leads there.
        var ambiguous = navigations
            .SelectMany(n => n.Names.Skip(1).Where(name => string.Equals(name, n.Target, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 || table.Columns.Any(c => string.Equals(c.Name, g.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return navigations
            .Select(n => n with
            {
                Names = n.Names
                    .Where((name, i) => i == 0 || n.Kind == NavigationKind.Reference || !ambiguous.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();
    }
}
