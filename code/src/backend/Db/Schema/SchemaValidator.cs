namespace Repere.Api.Db.Schema;

public class SchemaValidator
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "group", "order", "by", "table", "column", "index",
        "user", "users", "join", "insert", "update", "delete", "create", "alter", "drop",
        "constraint", "primary", "key", "foreign", "references", "values", "limit", "offset"
    };

    public SchemaValidationResult Validate(ModelJson model)
    {
        var errors = new List<SchemaValidationIssue>();
        var warnings = new List<SchemaValidationIssue>();
        var normalizedTables = new List<TableDef>();

        var tableLookup = new Dictionary<string, TableDef>(StringComparer.OrdinalIgnoreCase);
        var duplicateTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var tableIndex = 0; tableIndex < model.Tables.Count; tableIndex++)
        {
            var table = model.Tables[tableIndex];
            var tablePath = $"tables[{tableIndex}]";

            if (string.IsNullOrWhiteSpace(table.Name))
            {
                errors.Add(new("missing_table_name", "Table name is required.", tablePath));
            }
            else
            {
                if (!tableLookup.TryAdd(table.Name, table))
                    duplicateTableNames.Add(table.Name);

                if (table.Name.Length > 63)
                    warnings.Add(new("long_table_name", $"Table '{table.Name}' exceeds PostgreSQL's 63 character identifier limit.", $"{tablePath}.name"));
                if (ReservedWords.Contains(table.Name))
                    warnings.Add(new("reserved_table_name", $"Table '{table.Name}' is a SQL reserved word and may require quoting.", $"{tablePath}.name"));
            }

            var normalizedColumns = new List<ColumnDef>();
            var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasPrimaryKey = false;
            var existingIdColumn = false;

            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var column = table.Columns[columnIndex];
                var columnPath = $"{tablePath}.columns[{columnIndex}]";

                if (string.IsNullOrWhiteSpace(column.Name))
                    errors.Add(new("missing_column_name", $"Column name is required in table '{table.Name}'.", $"{columnPath}.name"));
                else
                {
                    if (!columnNames.Add(column.Name))
                        errors.Add(new("duplicate_column_name", $"Table '{table.Name}' contains duplicate column name '{column.Name}'.", $"{columnPath}.name"));

                    if (column.Name.Length > 63)
                        warnings.Add(new("long_column_name", $"Column '{table.Name}.{column.Name}' exceeds PostgreSQL's 63 character identifier limit.", $"{columnPath}.name"));
                    if (ReservedWords.Contains(column.Name))
                        warnings.Add(new("reserved_column_name", $"Column '{table.Name}.{column.Name}' is a SQL reserved word and may require quoting.", $"{columnPath}.name"));
                    if (string.Equals(column.Name, "id", StringComparison.OrdinalIgnoreCase))
                        existingIdColumn = true;
                }

                if (!column.TypeSpecified)
                    errors.Add(new("missing_column_type", $"Column '{table.Name}.{column.Name}' is missing a required type.", $"{columnPath}.type"));

                if (column.Type == ColumnType.Enum && column.Values.Count == 0)
                    errors.Add(new("enum_values_required", $"Enum column '{table.Name}.{column.Name}' must define a non-empty 'values' array.", $"{columnPath}.values"));

                if (column.Type == ColumnType.Reference && string.IsNullOrWhiteSpace(column.References))
                    errors.Add(new("reference_target_required", $"Reference column '{table.Name}.{column.Name}' must define 'references'.", $"{columnPath}.references"));

                if (column.Type == ColumnType.Computed && column.Formula is null)
                    errors.Add(new("computed_formula_required", $"Computed column '{table.Name}.{column.Name}' must define a 'formula' field.", $"{columnPath}.formula"));

                if (column.PrimaryKey)
                    hasPrimaryKey = true;

                normalizedColumns.Add(column);
            }

            if (!hasPrimaryKey)
            {
                if (existingIdColumn)
                {
                    errors.Add(new("missing_primary_key", $"Table '{table.Name}' has an 'id' column but no primary key. Mark 'id' as primaryKey or rename it.", $"{tablePath}.columns"));
                }
                else
                {
                    normalizedColumns.Insert(0, new ColumnDef
                    {
                        Name = "id",
                        Type = ColumnType.Integer,
                        TypeSpecified = true,
                        PrimaryKey = true,
                        Nullable = false,
                        Unique = true
                    });
                    hasPrimaryKey = true;
                }
            }

            if (normalizedColumns.Count == 1 &&
                string.Equals(normalizedColumns[0].Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new("id_only_table", $"Table '{table.Name}' currently contains no columns besides the primary key.", $"{tablePath}.columns"));
            }

            normalizedTables.Add(table with { Columns = normalizedColumns });
        }

        foreach (var duplicateName in duplicateTableNames)
        {
            errors.Add(new("duplicate_table_name", $"Schema contains duplicate table name '{duplicateName}'.", "tables"));
        }

        var normalizedModel = model with { Tables = normalizedTables };
        var normalizedLookup = normalizedModel.Tables
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        ValidateReferences(normalizedModel, normalizedLookup, errors);
        ValidateRelations(normalizedModel, normalizedLookup, errors);
        ValidateCircularDependencies(normalizedModel, normalizedLookup, errors);

        return new SchemaValidationResult(normalizedModel, errors, warnings);
    }

    private static void ValidateReferences(
        ModelJson model,
        Dictionary<string, TableDef> tableLookup,
        List<SchemaValidationIssue> errors)
    {
        for (var tableIndex = 0; tableIndex < model.Tables.Count; tableIndex++)
        {
            var table = model.Tables[tableIndex];
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var column = table.Columns[columnIndex];
                if (column.Type != ColumnType.Reference || string.IsNullOrWhiteSpace(column.References))
                    continue;

                if (!tableLookup.ContainsKey(column.References))
                {
                    errors.Add(new(
                        "reference_target_missing",
                        $"Reference column '{table.Name}.{column.Name}' points to unknown table '{column.References}'.",
                        $"tables[{tableIndex}].columns[{columnIndex}].references"));
                }
            }
        }
    }

    private static void ValidateRelations(
        ModelJson model,
        Dictionary<string, TableDef> tableLookup,
        List<SchemaValidationIssue> errors)
    {
        var relationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var junctionPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var relationIndex = 0; relationIndex < model.Relations.Count; relationIndex++)
        {
            var relation = model.Relations[relationIndex];
            var relationPath = $"relations[{relationIndex}]";

            if (string.IsNullOrWhiteSpace(relation.Name))
                errors.Add(new("missing_relation_name", "Relation name is required.", $"{relationPath}.name"));
            else if (!relationNames.Add(relation.Name))
                errors.Add(new("duplicate_relation_name", $"Schema contains duplicate relation name '{relation.Name}'.", $"{relationPath}.name"));
            if (!relation.TypeSpecified)
                errors.Add(new("missing_relation_type", $"Relation '{relation.Name}' is missing a required type.", $"{relationPath}.type"));
            if (string.IsNullOrWhiteSpace(relation.From))
                errors.Add(new("missing_relation_from", $"Relation '{relation.Name}' must define 'from'.", $"{relationPath}.from"));
            if (string.IsNullOrWhiteSpace(relation.To))
                errors.Add(new("missing_relation_to", $"Relation '{relation.Name}' must define 'to'.", $"{relationPath}.to"));

            var fromExists = !string.IsNullOrWhiteSpace(relation.From) && tableLookup.ContainsKey(relation.From);
            var toExists = !string.IsNullOrWhiteSpace(relation.To) && tableLookup.ContainsKey(relation.To);

            if (!fromExists && !string.IsNullOrWhiteSpace(relation.From))
                errors.Add(new("relation_from_missing", $"Relation '{relation.Name}' refers to unknown table '{relation.From}'.", $"{relationPath}.from"));
            if (!toExists && !string.IsNullOrWhiteSpace(relation.To))
                errors.Add(new("relation_to_missing", $"Relation '{relation.Name}' refers to unknown table '{relation.To}'.", $"{relationPath}.to"));

            if (relation.Type == RelationType.OneToMany)
            {
                if (string.IsNullOrWhiteSpace(relation.Column))
                {
                    errors.Add(new("relation_column_required", $"1:N relation '{relation.Name}' must define 'column'.", $"{relationPath}.column"));
                }
                else if (toExists)
                {
                    var targetTable = tableLookup[relation.To];
                    var column = targetTable.Columns.FirstOrDefault(c => string.Equals(c.Name, relation.Column, StringComparison.OrdinalIgnoreCase));
                    if (column is null)
                    {
                        errors.Add(new("relation_column_missing", $"Relation '{relation.Name}' expects column '{relation.Column}' on table '{relation.To}'.", $"{relationPath}.column"));
                    }
                    else
                    {
                        if (column.Type != ColumnType.Reference)
                        {
                            errors.Add(new("relation_column_not_reference", $"Relation '{relation.Name}' requires '{relation.To}.{column.Name}' to be a reference column.", $"{relationPath}.column"));
                        }

                        if (!string.Equals(column.References, relation.From, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new("relation_reference_mismatch", $"Relation '{relation.Name}' requires '{relation.To}.{column.Name}' to reference '{relation.From}'.", $"{relationPath}.column"));
                        }
                    }
                }
            }
            else if (relation.Type == RelationType.ManyToMany && fromExists && toExists)
            {
                var orderedPair = string.Compare(relation.From, relation.To, StringComparison.OrdinalIgnoreCase) <= 0
                    ? $"{relation.From}|{relation.To}"
                    : $"{relation.To}|{relation.From}";

                if (junctionPairs.TryGetValue(orderedPair, out var existingRelation))
                {
                    errors.Add(new("duplicate_many_to_many_pair", $"N:M relation '{relation.Name}' duplicates the junction pair already used by '{existingRelation}'.", relationPath));
                }
                else
                {
                    junctionPairs[orderedPair] = relation.Name;
                }
            }
        }
    }

    private static void ValidateCircularDependencies(
        ModelJson model,
        Dictionary<string, TableDef> tableLookup,
        List<SchemaValidationIssue> errors)
    {
        var graph = tableLookup.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        foreach (var table in model.Tables)
        {
            if (string.IsNullOrWhiteSpace(table.Name) || !graph.ContainsKey(table.Name))
                continue;

            foreach (var column in table.Columns.Where(c => c.Type == ColumnType.Reference && !string.IsNullOrWhiteSpace(c.References)))
            {
                if (!string.Equals(table.Name, column.References, StringComparison.OrdinalIgnoreCase) &&
                    tableLookup.ContainsKey(column.References!))
                {
                    graph[table.Name].Add(column.References!);
                }
            }
        }

        foreach (var relation in model.Relations.Where(r => r.Type == RelationType.OneToMany))
        {
            if (string.IsNullOrWhiteSpace(relation.From) || string.IsNullOrWhiteSpace(relation.To))
                continue;

            if (!string.Equals(relation.From, relation.To, StringComparison.OrdinalIgnoreCase) &&
                tableLookup.ContainsKey(relation.From) &&
                tableLookup.ContainsKey(relation.To))
            {
                graph[relation.To].Add(relation.From);
            }
        }

        var state = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();

        foreach (var tableName in graph.Keys)
        {
            if (DetectCycle(tableName, graph, state, stack, out var cycle))
            {
                errors.Add(new(
                    "circular_dependency",
                    $"Schema contains a circular dependency that prevents table creation order resolution: {string.Join(" -> ", cycle)}.",
                    "tables"));
                return;
            }
        }
    }

    private static bool DetectCycle(
        string node,
        Dictionary<string, HashSet<string>> graph,
        Dictionary<string, VisitState> state,
        Stack<string> stack,
        out IReadOnlyList<string> cycle)
    {
        if (state.TryGetValue(node, out var currentState))
        {
            if (currentState == VisitState.Visiting)
            {
                var path = stack.Reverse().ToList();
                var startIndex = path.FindIndex(n => string.Equals(n, node, StringComparison.OrdinalIgnoreCase));
                cycle = path.Skip(startIndex).Append(node).ToList();
                return true;
            }

            cycle = [];
            return false;
        }

        state[node] = VisitState.Visiting;
        stack.Push(node);

        foreach (var dependency in graph[node])
        {
            if (DetectCycle(dependency, graph, state, stack, out cycle))
                return true;
        }

        stack.Pop();
        state[node] = VisitState.Visited;
        cycle = [];
        return false;
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}

public sealed record SchemaValidationIssue(string Code, string Message, string Path);

public sealed class SchemaValidationResult
{
    public SchemaValidationResult(
        ModelJson normalizedModel,
        IReadOnlyList<SchemaValidationIssue> errors,
        IReadOnlyList<SchemaValidationIssue> warnings)
    {
        NormalizedModel = normalizedModel;
        Errors = errors;
        Warnings = warnings;
    }

    public ModelJson NormalizedModel { get; }
    public IReadOnlyList<SchemaValidationIssue> Errors { get; }
    public IReadOnlyList<SchemaValidationIssue> Warnings { get; }
    public bool IsValid => Errors.Count == 0;
}
