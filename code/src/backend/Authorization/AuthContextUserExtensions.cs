using System.Globalization;
using System.Security.Claims;

namespace Bento.Api.Authorization;

public static class AuthContextUserExtensions
{
    public static int? GetUserId(this ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");

        return int.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static string? GetRole(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Role)
            ?? principal.FindFirstValue("role");
    }
}
