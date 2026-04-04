using System.Text.Json;
using System.Text.Json.Serialization;

namespace Repere.Api.Db.Schema;

public static class ModelJsonSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static ModelJson Parse(string json)
    {
        var model = JsonSerializer.Deserialize<ModelJson>(json, JsonOptions);
        return model ?? throw new JsonException("Failed to deserialize Model JSON.");
    }

    public static string Serialize(ModelJson model)
    {
        return JsonSerializer.Serialize(model, JsonOptions);
    }

    internal static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = null,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }.WithConverters();
    }
}

internal static class ModelJsonSerializerOptionsExtensions
{
    public static JsonSerializerOptions WithConverters(this JsonSerializerOptions options)
    {
        options.Converters.Add(new ColumnTypeJsonConverter());
        options.Converters.Add(new RelationTypeJsonConverter());
        return options;
    }
}
