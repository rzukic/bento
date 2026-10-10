using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Bento.Api.Exceptions;
using Bento.Api.Middleware;

namespace Bento.Tests;

public sealed class ErrorHandlingMiddlewareTests
{
    [Fact]
    public async Task ErrorHandlingMiddleware_MapsValidationException_ToStandardErrorPayload()
    {
        var middleware = CreateMiddleware(
            _ => throw new ApiValidationException(
                "Email is required.",
                new Dictionary<string, object?>
                {
                    ["field"] = "email"
                }),
            Environments.Production);

        var context = CreateHttpContext("req_validation");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        var payload = await ReadErrorAsync(context);
        Assert.Equal("VALIDATION_FAILED", payload.Error.Code);
        Assert.Equal("Email is required.", payload.Error.Message);
        Assert.Equal("email", payload.Error.Details!["field"].GetString());
        Assert.Equal("req_validation", payload.Error.Details["requestId"].GetString());
        Assert.DoesNotContain("stackTrace", payload.Error.Details.Keys);
    }

    [Fact]
    public async Task ErrorHandlingMiddleware_MapsKnownExceptions_ToExpectedStatuses()
    {
        var testCases = new (Exception Exception, int StatusCode, string Code)[]
        {
            (new ApiNotFoundException("Missing."), StatusCodes.Status404NotFound, "NOT_FOUND"),
            (new ApiUnauthorizedException("Login required."), StatusCodes.Status401Unauthorized, "UNAUTHORIZED"),
            (new ApiForbiddenException("No access."), StatusCodes.Status403Forbidden, "FORBIDDEN"),
            (new ApiConflictException("Already exists."), StatusCodes.Status409Conflict, "CONFLICT"),
            (new ApiTooManyRequestsException("Slow down."), StatusCodes.Status429TooManyRequests, "TOO_MANY_REQUESTS")
        };

        foreach (var testCase in testCases)
        {
            var middleware = CreateMiddleware(_ => throw testCase.Exception, Environments.Production);
            var context = CreateHttpContext("req_known");

            await middleware.InvokeAsync(context);

            Assert.Equal(testCase.StatusCode, context.Response.StatusCode);
            var payload = await ReadErrorAsync(context);
            Assert.Equal(testCase.Code, payload.Error.Code);
        }
    }

    [Fact]
    public async Task ErrorHandlingMiddleware_MapsUnhandledException_ToInternalError_WithoutDebugDetailsInProduction()
    {
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"), Environments.Production);
        var context = CreateHttpContext("req_prod");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        var payload = await ReadErrorAsync(context);
        Assert.Equal("INTERNAL_ERROR", payload.Error.Code);
        Assert.Equal("An unexpected error occurred.", payload.Error.Message);
        Assert.Equal("req_prod", payload.Error.Details!["requestId"].GetString());
        Assert.DoesNotContain("exceptionType", payload.Error.Details.Keys);
        Assert.DoesNotContain("stackTrace", payload.Error.Details.Keys);
    }

    [Fact]
    public async Task ErrorHandlingMiddleware_MapsDatabaseTimeouts_ToTimeout()
    {
        var middleware = CreateMiddleware(
            _ => throw new InvalidOperationException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt")),
            Environments.Production);
        var context = CreateHttpContext("req_timeout");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        var payload = await ReadErrorAsync(context);
        Assert.Equal("TIMEOUT", payload.Error.Code);
        Assert.Contains("took too long", payload.Error.Message);
        Assert.Equal("req_timeout", payload.Error.Details!["requestId"].GetString());
    }

    [Fact]
    public async Task ErrorHandlingMiddleware_IncludesDebugDetails_ForUnhandledExceptionsInDevelopment()
    {
        var middleware = CreateMiddleware(_ => throw new InvalidOperationException("boom"), Environments.Development);
        var context = CreateHttpContext("req_dev");

        await middleware.InvokeAsync(context);

        var payload = await ReadErrorAsync(context);
        Assert.Equal("INTERNAL_ERROR", payload.Error.Code);
        Assert.Equal("req_dev", payload.Error.Details!["requestId"].GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, payload.Error.Details["exceptionType"].GetString());
        Assert.True(payload.Error.Details["stackTrace"].GetString()!.Length > 0);
    }

    private static ErrorHandlingMiddleware CreateMiddleware(RequestDelegate next, string environmentName)
    {
        return new ErrorHandlingMiddleware(
            next,
            new TestLogger<ErrorHandlingMiddleware>(),
            new TestHostEnvironment(environmentName));
    }

    private static DefaultHttpContext CreateHttpContext(string requestId)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = requestId;
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/admin/health";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<ErrorEnvelopeJson> ReadErrorAsync(DefaultHttpContext context)
    {
        return await TestHttpContextJson.ReadBodyAsync<ErrorEnvelopeJson>(context);
    }
}
