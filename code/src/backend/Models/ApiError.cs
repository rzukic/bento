namespace Repere.Api.Models;

public enum ApiErrorCode
{
    ValidationFailed,
    NotFound,
    Unauthorized,
    Forbidden,
    Conflict,
    InternalError
}

public sealed record ApiErrorEnvelope(ApiError Error);

public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Details = null);

public static class ApiErrorCodeExtensions
{
    public static string ToStringCode(this ApiErrorCode code)
    {
        return code switch
        {
            ApiErrorCode.ValidationFailed => "VALIDATION_FAILED",
            ApiErrorCode.NotFound => "NOT_FOUND",
            ApiErrorCode.Unauthorized => "UNAUTHORIZED",
            ApiErrorCode.Forbidden => "FORBIDDEN",
            ApiErrorCode.Conflict => "CONFLICT",
            ApiErrorCode.InternalError => "INTERNAL_ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };
    }
}
