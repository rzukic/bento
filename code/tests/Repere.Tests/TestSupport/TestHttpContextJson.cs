using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Repere.Tests;

public static class TestHttpContextJson
{
    public static async Task<T> ReadBodyAsync<T>(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;

        var payload = await JsonSerializer.DeserializeAsync<T>(
            context.Response.Body,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        return Assert.IsType<T>(payload);
    }
}

public sealed record ErrorEnvelopeJson(ErrorBodyJson Error);

public sealed record ErrorBodyJson(
    string Code,
    string Message,
    Dictionary<string, JsonElement>? Details);
