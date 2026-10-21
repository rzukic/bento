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

    /// <summary>The caller's role in the workspace of the request (Step 5.5), or for the installation's own parts their installation role.</summary>
    public static string? GetRole(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Role)
            ?? principal.FindFirstValue("role");
    }

    /// <summary>The user's own role, the same in every workspace: <c>admin</c> runs the installation.</summary>
    public static string? GetInstallationRole(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("installation_role") ?? principal.GetRole();

    /// <summary>
    /// Runs the installation: users, workspaces, the vault, channels, AI. A
    /// workspace's admin is not one — vault credentials, for one, stay with them.
    /// </summary>
    public static bool IsInstallationAdmin(this ClaimsPrincipal principal) => principal.GetInstallationRole() == "admin";
}
