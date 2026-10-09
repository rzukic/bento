using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bento.Api.Formula.Sandbox;
using Bento.Api.Services;

namespace Bento.Api.Formula;

/// <summary>
/// <c>CUSTOM_ROWS("name", PARTITION(…), ORDER(…), …)</c> (Step 4.3b): the rows
/// of a table, grouped by the partition values and sorted by the order values
/// (then the key), go to the function at once. The function returns a value per
/// row, so a value may depend on the rows before it — a running average cost.
/// </summary>
public static class CustomRows
{
    /// <summary>One group's rows in order: keys, the arguments per row, and a hash of both.</summary>
    public sealed record Group(IReadOnlyList<string> Keys, IReadOnlyList<IReadOnlyList<object?>> Rows, string Inputs);

    public sealed record Result(string Key, object? Value, string? Error, string Inputs);

    /// <summary>
    /// SELECT of key, partition and arguments, in order. Empty order values come
    /// first on both providers; the raw key breaks ties (numerically for number keys).
    /// </summary>
    public static string SelectSql(CustomCall custom, string source, string keyColumn, string? extraSelect = null, string? join = null)
    {
        var columns = new List<string> { $"CAST({source}.{keyColumn} AS TEXT) AS \"__key\"" };
        columns.AddRange(custom.Partition!.Select((p, i) => $"{p.Sql} AS \"__p{i}\""));
        columns.AddRange(custom.Arguments.Select((a, i) => $"{a.Sql} AS \"__a{i}\""));
        if (extraSelect is not null)
            columns.Add(extraSelect);
        var order = custom.Order!
            .SelectMany(o => new[] { $"CASE WHEN {o.Sql} IS NULL THEN 0 ELSE 1 END", o.Sql })
            .Append($"{source}.{keyColumn}");
        return $"SELECT {string.Join(", ", columns)} FROM {source}{(join is null ? "" : " " + join)} ORDER BY {string.Join(", ", order)}";
    }

    /// <summary>The selected rows grouped by partition; rows keep the query's order within a group.</summary>
    public static IReadOnlyList<Group> Groups(CustomCall custom, IEnumerable<IDictionary<string, object?>> rows) =>
        rows.GroupBy(row => JsonSerializer.Serialize(custom.Partition!.Select((p, i) => ResultValues.Shape(row[$"__p{i}"], p.Type))))
            .Select(group =>
            {
                var keys = group.Select(r => System.Convert.ToString(r["__key"], CultureInfo.InvariantCulture)!).ToList();
                var values = group
                    .Select(r => (IReadOnlyList<object?>)custom.Arguments.Select((a, i) => ResultValues.Shape(r[$"__a{i}"], a.Type)).ToList())
                    .ToList();
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { keys, values }))));
                return new Group(keys, values, "rows:" + hash[..32]);
            })
            .ToList();

    /// <summary>Runs the function over one group: the arguments as lists, one value back per row.</summary>
    public static IReadOnlyList<Result> Run(JsFunction? function, string? createError, CustomCall custom, Group group)
    {
        if (function is null)
            return group.Keys.Select(k => new Result(k, null, createError, group.Inputs)).ToList();
        var columns = custom.Arguments
            .Select((_, i) => (IReadOnlyList<object?>)group.Rows.Select(r => r[i]).ToList())
            .ToList();
        var (values, error, _) = function.CallRows(columns, group.Keys.Count);
        return group.Keys
            .Select((k, i) => new Result(k, values is null ? null : values[i], values is null ? error : null, group.Inputs))
            .ToList();
    }
}
