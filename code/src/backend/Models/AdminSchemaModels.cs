using System.Text.Json.Serialization;
using Repere.Api.Db.Schema;

namespace Repere.Api.Models;

public sealed record AdminSchemaIssueResponse(string Code, string Message, string Path);

public sealed record AdminSchemaChangeResponse
{
    public required string ChangeType { get; init; }
    public required string Table { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousTable { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Column { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousColumn { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Relation { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Index { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? OldValue { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? NewValue { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoercionKind { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AdminSchemaCastExampleResponse>? CastExamples { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdminSchemaChangeImpactResponse? Impact { get; init; }

    public bool IsDestructive { get; init; }
}

public sealed record AdminSchemaCastExampleResponse
{
    public object? SourceValue { get; init; }
    public object? CastValue { get; init; }
    public required bool IsValid { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

public sealed record AdminSchemaChangeImpactResponse
{
    public required string Kind { get; init; }
    public required string ObjectType { get; init; }
    public required string ObjectName { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RowCount { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? SampleRows { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<object?>? SampleValues { get; init; }

    public required string Message { get; init; }
}

public sealed record AdminSchemaPlanResponse
{
    public required IReadOnlyList<string> SqlUp { get; init; }
    public required IReadOnlyList<string> SqlDown { get; init; }
    public required bool IsDestructive { get; init; }
    public required bool RequiresConfirmation { get; init; }
}

public sealed record AdminSchemaPreviewResponse
{
    public required bool IsValid { get; init; }
    public required bool HasChanges { get; init; }
    public required bool MetadataOnly { get; init; }
    public required bool IsDestructive { get; init; }
    public required bool RequiresConfirmation { get; init; }
    public required int CurrentVersion { get; init; }
    public required IReadOnlyList<AdminSchemaIssueResponse> Errors { get; init; }
    public required IReadOnlyList<AdminSchemaIssueResponse> Warnings { get; init; }
    public required IReadOnlyList<AdminSchemaChangeResponse> Changes { get; init; }
    public required ModelJson NormalizedModel { get; init; }

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdminSchemaPlanResponse? Plan { get; init; }
}
