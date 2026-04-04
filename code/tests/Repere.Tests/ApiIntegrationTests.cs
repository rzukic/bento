using System.Net;
using System.Net.Http.Json;
using System.Text;
using Repere.Api.Models;

namespace Repere.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task Get_AdminHealth_ReturnsHealthyJson_AndGeneratedRequestId()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync();

        using var response = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var values));

        var requestId = Assert.Single(values);
        Assert.False(string.IsNullOrWhiteSpace(requestId));

        var payload = await response.Content.ReadFromJsonAsync<AdminHealthResponse>();
        payload = Assert.IsType<AdminHealthResponse>(payload);

        Assert.Equal("healthy", payload.Status);
        Assert.Equal("sqlite", payload.Database);
        Assert.Equal("open", payload.SystemPool);
        Assert.Equal("open", payload.DataPool);
        Assert.Null(payload.Error);
    }

    [Fact]
    public async Task Get_AdminHealth_ReusesIncomingRequestId_AndRespondsOnVersionedRoute()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/health");
        request.Headers.TryAddWithoutValidation("X-Request-Id", "req_http_integration_123");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var values));
        Assert.Equal("req_http_integration_123", Assert.Single(values));
    }

    [Fact]
    public async Task Get_RootHealth_ReturnsNotFound_WhileVersionedHealthExists()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync();

        using var rootResponse = await host.Client.GetAsync("/health");
        using var versionedResponse = await host.Client.GetAsync("/api/v1/admin/health");

        Assert.Equal(HttpStatusCode.NotFound, rootResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versionedResponse.StatusCode);
    }

    [Fact]
    public async Task Post_AdminHealth_WithOversizedJson_ReturnsStandardPayloadTooLargeError()
    {
        await using var host = await ApiIntegrationTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Backend:MaxBodySize"] = "1kb"
        });

        var oversizedJson = "\"" + new string('x', 2048) + "\"";
        using var content = new StringContent(oversizedJson, Encoding.UTF8, "application/json");

        using var response = await host.Client.PostAsync("/api/v1/admin/health", content);

        Assert.Equal((HttpStatusCode)413, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>();
        payload = Assert.IsType<ApiErrorEnvelope>(payload);

        Assert.Equal("VALIDATION_FAILED", payload.Error.Code);
        Assert.Contains("max size", payload.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(payload.Error.Details);
        Assert.Equal("1kb", Assert.IsType<System.Text.Json.JsonElement>(payload.Error.Details!["maxBodySize"]).GetString());
    }
}
