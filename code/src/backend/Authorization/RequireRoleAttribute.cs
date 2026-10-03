using Microsoft.AspNetCore.Mvc.Filters;
using Bento.Api.Exceptions;

namespace Bento.Api.Authorization;

/// <summary>
/// Requires the caller to satisfy at least one of the given roles.
/// Multiple attributes on the same endpoint are combined with AND; multiple
/// roles inside one attribute are combined with OR.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireRoleAttribute : Attribute, IAsyncAuthorizationFilter
{
    public RequireRoleAttribute(params string[] roles)
    {
        var normalized = (roles ?? Array.Empty<string>())
            .Select(NormalizeRole)
            .Where(role => role is not null)
            .Select(role => role!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
            throw new ArgumentException("At least one role is required.", nameof(roles));

        AcceptedRoles = normalized;
    }

    public IReadOnlyList<string> AcceptedRoles { get; }

    /// <summary>
    /// The first accepted role. Kept for callers that only ever passed a single role.
    /// </summary>
    public string RequiredRole => AcceptedRoles[0];

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var principal = context.HttpContext.User;
        var actualRole = NormalizeRole(principal.GetRole());

        if (actualRole is null || !AcceptedRoles.Any(required => RoleSatisfies(actualRole, required)))
        {
            var requiredLabel = string.Join("' or '", AcceptedRoles);
            throw new ApiForbiddenException(
                $"Role '{requiredLabel}' is required for this endpoint.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requiredRole"] = AcceptedRoles.Count == 1 ? AcceptedRoles[0] : AcceptedRoles,
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
