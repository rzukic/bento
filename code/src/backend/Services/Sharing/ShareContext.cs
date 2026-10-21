namespace Bento.Api.Services.Sharing;

public static class ShareContext
{
    /// <summary>The share link of the request when a link's visitor makes it (Step 5.6), else null.</summary>
    public static ShareService.Visitor? ShareVisitor(this HttpContext context) =>
        context.Items.TryGetValue(Middleware.AuthMiddleware.ShareItem, out var value) ? value as ShareService.Visitor : null;
}
