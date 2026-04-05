using Repere.Api.Models;

namespace Repere.Api.Exceptions;

public abstract class ApiException : Exception
{
    protected ApiException(
        ApiErrorCode code,
        string message,
        int statusCode,
        IReadOnlyDictionary<string, object?>? details = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
        Details = details;
    }

    public ApiErrorCode Code { get; }

    public int StatusCode { get; }

    public IReadOnlyDictionary<string, object?>? Details { get; }
}

public sealed class ApiValidationException : ApiException
{
    public ApiValidationException(string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(ApiErrorCode.ValidationFailed, message, StatusCodes.Status400BadRequest, details)
    {
    }
}

public sealed class ApiNotFoundException : ApiException
{
    public ApiNotFoundException(string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(ApiErrorCode.NotFound, message, StatusCodes.Status404NotFound, details)
    {
    }
}

public sealed class ApiUnauthorizedException : ApiException
{
    public ApiUnauthorizedException(string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(ApiErrorCode.Unauthorized, message, StatusCodes.Status401Unauthorized, details)
    {
    }
}

public sealed class ApiForbiddenException : ApiException
{
    public ApiForbiddenException(string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(ApiErrorCode.Forbidden, message, StatusCodes.Status403Forbidden, details)
    {
    }
}

public sealed class ApiConflictException : ApiException
{
    public ApiConflictException(string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(ApiErrorCode.Conflict, message, StatusCodes.Status409Conflict, details)
    {
    }
}

public sealed class ApiTooManyRequestsException : ApiException
{
    public ApiTooManyRequestsException(string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(ApiErrorCode.TooManyRequests, message, StatusCodes.Status429TooManyRequests, details)
    {
    }
}

public sealed class ApiRequestTooLargeException : ApiException
{
    public ApiRequestTooLargeException(string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(ApiErrorCode.ValidationFailed, message, StatusCodes.Status413PayloadTooLarge, details)
    {
    }
}
