using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Bento.Api.Configuration;
using Bento.Api.Middleware;

namespace Bento.Tests;

public sealed class CorsAndBodyLimitMiddlewareTests
{
    [Fact]
    public async Task CorsPolicyMiddleware_AllowsConfiguredOrigin_AndSetsCorsHeaders()
    {
        var middleware = new CorsPolicyMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            CreateRuntimeOptions(allowedOrigins: ["https://app.example.com"]));

        var context = CreateHttpContext();
        context.Request.Headers["Origin"] = "https://app.example.com";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("https://app.example.com", context.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal(RequestIdMiddleware.HeaderName, context.Response.Headers["Access-Control-Expose-Headers"].ToString());
        Assert.Equal("Origin", context.Response.Headers["Vary"].ToString());
    }

    [Fact]
    public async Task CorsPolicyMiddleware_AllowsSameOrigin_WhenAllowListIsEmpty()
    {
        var middleware = new CorsPolicyMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            CreateRuntimeOptions());

        var context = CreateHttpContext();
        context.Request.Headers["Origin"] = "https://api.example.com";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("https://api.example.com", context.Response.Headers["Access-Control-Allow-Origin"].ToString());
    }

    [Fact]
    public async Task CorsPolicyMiddleware_BlocksDisallowedOrigin_WithStandardErrorEnvelope()
    {
        var context = CreateHttpContext();
        context.TraceIdentifier = "req_cors_blocked";
        context.Request.Headers["Origin"] = "https://evil.example.com";

        var pipeline = CreateErrorWrappedCorsPipeline(CreateRuntimeOptions());

        await pipeline(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        var payload = await ReadErrorAsync(context);
        Assert.Equal("FORBIDDEN", payload.Error.Code);
        Assert.Equal("https://evil.example.com", payload.Error.Details!["origin"].GetString());
        Assert.Equal("req_cors_blocked", payload.Error.Details["requestId"].GetString());
    }

    [Fact]
    public async Task CorsPolicyMiddleware_HandlesAllowedPreflightRequest()
    {
        var middleware = new CorsPolicyMiddleware(
            _ => Task.CompletedTask,
            CreateRuntimeOptions(allowedOrigins: ["https://app.example.com"]));

        var context = CreateHttpContext();
        context.Request.Method = HttpMethods.Options;
        context.Request.Headers["Origin"] = "https://app.example.com";
        context.Request.Headers["Access-Control-Request-Method"] = HttpMethods.Post;
        context.Request.Headers["Access-Control-Request-Headers"] = "Content-Type, Authorization";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.Equal("https://app.example.com", context.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Contains(HttpMethods.Post, context.Response.Headers["Access-Control-Allow-Methods"].ToString(), StringComparison.Ordinal);
        Assert.Equal("Content-Type, Authorization", context.Response.Headers["Access-Control-Allow-Headers"].ToString());
    }

    [Fact]
    public async Task BodySizeLimitMiddleware_RejectsOversizedJsonRequest_WithStandardErrorEnvelope()
    {
        var context = CreateHttpContext();
        context.TraceIdentifier = "req_body_limit";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = 2048;
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new TestMaxBodySizeFeature());

        var pipeline = CreateErrorWrappedBodyLimitPipeline(CreateRuntimeOptions(maxBodySize: "1kb"));

        await pipeline(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        var payload = await ReadErrorAsync(context);
        Assert.Equal("VALIDATION_FAILED", payload.Error.Code);
        Assert.Equal("req_body_limit", payload.Error.Details!["requestId"].GetString());
        Assert.Equal("1kb", payload.Error.Details["maxBodySize"].GetString());
        Assert.Equal(2048, payload.Error.Details["contentLength"].GetInt64());
    }

    [Fact]
    public async Task BodySizeLimitMiddleware_AllowsJsonRequest_WithinLimit_AndSetsServerFeature()
    {
        var feature = new TestMaxBodySizeFeature();
        var middleware = new BodySizeLimitMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            CreateRuntimeOptions(maxBodySize: "2kb"));

        var context = CreateHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = 256;
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(2048, feature.MaxRequestBodySize);
    }

    private static RequestDelegate CreateErrorWrappedCorsPipeline(BackendRuntimeOptions runtimeOptions)
    {
        var cors = new CorsPolicyMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            runtimeOptions);

        var errorHandling = new ErrorHandlingMiddleware(
            context => cors.InvokeAsync(context),
            new TestLogger<ErrorHandlingMiddleware>(),
            new TestHostEnvironment(Environments.Production));

        return context => errorHandling.InvokeAsync(context);
    }

    private static RequestDelegate CreateErrorWrappedBodyLimitPipeline(BackendRuntimeOptions runtimeOptions)
    {
        var bodyLimit = new BodySizeLimitMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            runtimeOptions);

        var errorHandling = new ErrorHandlingMiddleware(
            context => bodyLimit.InvokeAsync(context),
            new TestLogger<ErrorHandlingMiddleware>(),
            new TestHostEnvironment(Environments.Production));

        return context => errorHandling.InvokeAsync(context);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.Request.Path = "/api/v1/admin/health";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static BackendRuntimeOptions CreateRuntimeOptions(string[]? allowedOrigins = null, string maxBodySize = "10mb")
    {
        return new BackendRuntimeOptions
        {
            Port = 5000,
            LogLevel = "Information",
            AllowedOrigins = allowedOrigins ?? [],
            MaxBodySize = maxBodySize,
            AuthMode = BackendAuthMode.Standard,
            DataDir = "./data"
        };
    }

    private static async Task<ErrorEnvelopeJson> ReadErrorAsync(DefaultHttpContext context)
    {
        return await TestHttpContextJson.ReadBodyAsync<ErrorEnvelopeJson>(context);
    }

    private sealed class TestMaxBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; set; }

        public long? MaxRequestBodySize { get; set; }
    }

}
