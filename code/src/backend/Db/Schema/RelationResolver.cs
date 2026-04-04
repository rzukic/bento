using System.Text.Json;

namespace Repere.Api.Db.Schema;

public sealed record ResolvedRelation
{
    public required RelationDef Definition { get; init; }
    public required string ForwardAccessor { get; init; }
    public required string ReverseAccessor { get; init; }
    public required bool IsSelfReferential { get; init; }
    public required bool RequiresDeferredConstraint { get; init; }
    public string? ForeignKeyColumn { get; init; }
    public string? JunctionTableName { get; init; }
    public TableDef? JunctionTable { get; init; }
}

public sealed record RelationResolutionResult
{
    public required IReadOnlyList<TableDef> OrderedTables { get; init; }
    public required IReadOnlyList<ResolvedRelation> Relations { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; }
}

public class RelationResolver
{
    public RelationResolutionResult Resolve(ModelJson model)
    {
        var tableLookup = model.Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var dependencies = BuildDependencies(model, tableLookup);
        var cycles = FindCycles(dependencies);
        var orderedTables = TopologicalSort(model.Tables, dependencies);

        var relations = model.Relations
            .Select(relation => ResolveRelation(relation, tableLookup, cycles))
            .ToList();

        return new RelationResolutionResult
        {
            OrderedTables = orderedTables,
            Relations = relations,
            Cycles = cycles
        };
    }

    public IReadOnlyList<TableDef> OrderTables(ModelJson model) => Resolve(model).OrderedTables;

    public ResolvedRelation ResolveRelation(RelationDef relation, IReadOnlyDictionary<string, TableDef> tableLookup)
    {
        return ResolveRelation(relation, tableLookup, []);
    }

    public string GetJunctionTableName(RelationDef relation)
    {
        var endpoints = GetOrderedRelationEndpoints(relation);
        return $"_jt_{endpoints.Left}_{endpoints.Right}";
    }

    public TableDef BuildJunctionTable(RelationDef relation)
    {
        var endpoints = GetOrderedRelationEndpoints(relation);
        return new TableDef
        {
            Name = GetJunctionTableName(relation),
            Columns =
            [
                BuildJunctionReferenceColumn(endpoints.Left),
                BuildJunctionReferenceColumn(endpoints.Right),
                new ColumnDef
                {
                    Name = "created_at",
                    Type = ColumnType.DateTime,
                    TypeSpecified = true,
                    Nullable = false,
                    AdditionalData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["defaultSql"] = ParseJsonString("CURRENT_TIMESTAMP")
                    }
                }
            ],
            Indexes = []
        };
    }

    private ResolvedRelation ResolveRelation(
        RelationDef relation,
        IReadOnlyDictionary<string, TableDef> tableLookup,
        IReadOnlyList<IReadOnlyList<string>> cycles)
    {
        var isSelfReferential = string.Equals(relation.From, relation.To, StringComparison.OrdinalIgnoreCase);
        var requiresDeferredConstraint = isSelfReferential || cycles.Any(cycle =>
            cycle.Any(name => string.Equals(name, relation.From, StringComparison.OrdinalIgnoreCase))
            && cycle.Any(name => string.Equals(name, relation.To, StringComparison.OrdinalIgnoreCase)));

        if (relation.Type == RelationType.ManyToMany)
        {
            var junctionTable = BuildJunctionTable(relation);
            return new ResolvedRelation
            {
                Definition = relation,
                ForwardAccessor = relation.Name,
                ReverseAccessor = relation.Name,
                IsSelfReferential = isSelfReferential,
                RequiresDeferredConstraint = requiresDeferredConstraint,
                JunctionTableName = junctionTable.Name,
                JunctionTable = junctionTable
            };
        }

        return new ResolvedRelation
        {
            Definition = relation,
            ForwardAccessor = SingularizeName(relation.From),
            ReverseAccessor = relation.Name,
            IsSelfReferential = isSelfReferential,
            RequiresDeferredConstraint = requiresDeferredConstraint,
            ForeignKeyColumn = relation.Column
        };
    }

    private static Dictionary<string, HashSet<string>> BuildDependencies(
        ModelJson model,
        IReadOnlyDictionary<string, TableDef> tableLookup)
    {
        var dependencies = model.Tables.ToDictionary(
            table => table.Name,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var table in model.Tables)
        {
            foreach (var column in table.Columns.Where(c => c.Type == ColumnType.Reference && !string.IsNullOrWhiteSpace(c.References)))
            {
                if (tableLookup.ContainsKey(column.References!) &&
                    !string.Equals(table.Name, column.References, StringComparison.OrdinalIgnoreCase))
                {
                    dependencies[table.Name].Add(column.References!);
                }
            }
        }

        foreach (var relation in model.Relations.Where(r => r.Type == RelationType.OneToMany))
        {
            if (string.IsNullOrWhiteSpace(relation.From) || string.IsNullOrWhiteSpace(relation.To))
                continue;

            if (tableLookup.ContainsKey(relation.From) &&
                tableLookup.ContainsKey(relation.To) &&
                !string.Equals(relation.From, relation.To, StringComparison.OrdinalIgnoreCase))
            {
                dependencies[relation.To].Add(relation.From);
            }
        }

        return dependencies;
    }

    private static IReadOnlyList<TableDef> TopologicalSort(
        IReadOnlyList<TableDef> tables,
        IReadOnlyDictionary<string, HashSet<string>> dependencies)
    {
        var incoming = dependencies.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Count,
            StringComparer.OrdinalIgnoreCase);
        var dependents = dependencies.Keys.ToDictionary(
            key => key,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (tableName, refs) in dependencies)
        {
            foreach (var dependency in refs)
            {
                if (dependents.TryGetValue(dependency, out var list))
                    list.Add(tableName);
            }
        }

        var orderIndex = tables
            .Select((table, index) => new { table.Name, Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var available = incoming
            .Where(kvp => kvp.Value == 0)
            .Select(kvp => kvp.Key)
            .OrderBy(name => orderIndex[name])
            .ToList();

        var ordered = new List<TableDef>();
        var queued = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);

        while (available.Count > 0)
        {
            var current = available[0];
            available.RemoveAt(0);
            queued.Remove(current);

            ordered.Add(tables.First(t => string.Equals(t.Name, current, StringComparison.OrdinalIgnoreCase)));

            foreach (var dependent in dependents[current].OrderBy(name => orderIndex[name]))
            {
                incoming[dependent]--;
                if (incoming[dependent] == 0 && queued.Add(dependent))
                    available.Add(dependent);
            }

            available.Sort((left, right) => orderIndex[left].CompareTo(orderIndex[right]));
        }

        foreach (var table in tables)
        {
            if (ordered.Any(t => string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            ordered.Add(table);
        }

        return ordered;
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindCycles(IReadOnlyDictionary<string, HashSet<string>> dependencies)
    {
        var cycles = new List<IReadOnlyList<string>>();
        var state = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();

        foreach (var tableName in dependencies.Keys)
        {
            DetectCycles(tableName, dependencies, state, stack, cycles);
        }

        return cycles;
    }

    private static void DetectCycles(
        string node,
        IReadOnlyDictionary<string, HashSet<string>> dependencies,
        Dictionary<string, VisitState> state,
        Stack<string> stack,
        List<IReadOnlyList<string>> cycles)
    {
        if (state.TryGetValue(node, out var currentState))
        {
            if (currentState == VisitState.Visiting)
            {
                var path = stack.Reverse().ToList();
                var startIndex = path.FindIndex(name => string.Equals(name, node, StringComparison.OrdinalIgnoreCase));
                var cycle = path.Skip(startIndex).Append(node).ToList();
                if (!cycles.Any(existing => existing.SequenceEqual(cycle, StringComparer.OrdinalIgnoreCase)))
                    cycles.Add(cycle);
            }

            return;
        }

        state[node] = VisitState.Visiting;
        stack.Push(node);

        foreach (var dependency in dependencies[node])
        {
            DetectCycles(dependency, dependencies, state, stack, cycles);
        }

        stack.Pop();
        state[node] = VisitState.Visited;
    }

    private static ColumnDef BuildJunctionReferenceColumn(string tableName)
    {
        return new ColumnDef
        {
            Name = $"{SingularizeName(tableName)}_id",
            Type = ColumnType.Reference,
            TypeSpecified = true,
            References = tableName,
            Nullable = false,
            PrimaryKey = true
        };
    }

    private static (string Left, string Right) GetOrderedRelationEndpoints(RelationDef relation)
    {
        return string.Compare(relation.From, relation.To, StringComparison.OrdinalIgnoreCase) <= 0
            ? (relation.From, relation.To)
            : (relation.To, relation.From);
    }

    private static JsonElement ParseJsonString(string value)
    {
        using var doc = JsonDocument.Parse($"\"{value}\"");
        return doc.RootElement.Clone();
    }

    private static string SingularizeName(string name)
    {
        if (name.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && name.Length > 3)
            return name[..^3] + "y";
        if (name.EndsWith("sses", StringComparison.OrdinalIgnoreCase))
            return name[..^2];
        if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase) && name.Length > 1 && !name.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            return name[..^1];
        return name;
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
