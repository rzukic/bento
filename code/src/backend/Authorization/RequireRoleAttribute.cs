using Microsoft.AspNetCore.Mvc.Filters;
using Repere.Api.Exceptions;

namespace Repere.Api.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireRoleAttribute : Attribute, IAsyncAuthorizationFilter
{
    public RequireRoleAttribute(string role)
    {
        RequiredRole = NormalizeRole(role)
            ?? throw new ArgumentException("Role is required.", nameof(role));
    }

    public string RequiredRole { get; }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var principal = context.HttpContext.User;
        var actualRole = NormalizeRole(principal.GetRole());

        if (actualRole is null || !RoleSatisfies(actualRole, RequiredRole))
        {
            throw new ApiForbiddenException(
                $"Role '{RequiredRole}' is required for this endpoint.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requiredRole"] = RequiredRole,
                    ["actualRole"] = actualRole
                });
        }

        return Task.CompletedTask;
    }

    private static bool RoleSatisfies(string actualRole, string requiredRole)
    {
        return actualRole switch
        {
            "admin" => requiredRole is "admin" or "editor" or "viewer",
            "editor" => requiredRole is "editor" or "viewer",
            "viewer" => requiredRole == "viewer",
            "api" => requiredRole == "api",
            _ => false
        };
    }

    private static string? NormalizeRole(string? role)
    {
        var normalized = role?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
