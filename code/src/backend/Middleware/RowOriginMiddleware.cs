using System.Security.Claims;
using Bento.Api.Services;

namespace Bento.Api.Middleware;

/// <summary>
/// Where a request's writes come from (Step 5.12), for row events: an API
/// key, an MCP client, a public form, a calendar, or a user in the app.
/// Imports set their own origin around the rows they write.
/// </summary>
public sealed class RowOriginMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using (Use(context))
            await next(context);
    }

    private static IDisposable Use(HttpContext context)
    {
        var user = context.User;
        var name = user.FindFirstValue(ClaimTypes.Name);
        var path = context.Request.Path;
        if (path.StartsWithSegments("/dav"))
            return RowOrigin.Use("calendar", null);
        if (path.StartsWithSegments("/mcp"))
            return RowOrigin.Use("mcp", name);
        return user.FindFirstValue("auth_method") switch
        {
            "api_key" => RowOrigin.Use("api", name),
            "oauth" => RowOrigin.Use("mcp", name),
            "share" when context.Items.TryGetValue(AuthMiddleware.ShareItem, out var item) && item is Services.Sharing.ShareService.Visitor visitor =>
                RowOrigin.Use("form", visitor.Link.Name),
            _ => RowOrigin.Use("app", name)
        };
    }
}
