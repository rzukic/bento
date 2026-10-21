using Bento.Api.Services.Mcp;

namespace Bento.Api.Services.Ai;

/// <summary>
/// A prompt plus what this workspace's templates add: their tables, and — when
/// the finance template is installed — its conventions. A workspace without
/// templates gets the prompt as it is: nothing about a use case it does not have.
/// </summary>
public static class TemplatePrompts
{
    public static async Task<string> WithTemplatesAsync(string prompt, TemplateRegistry? templates, bool includeGuide = true)
    {
        if (templates is null)
            return prompt;
        var context = await templates.ContextAsync();
        if (context is null)
            return prompt;
        var parts = new List<string> { prompt, context };
        if (includeGuide && await templates.HasAsync("finance"))
            parts.Add("Reference — the finance template's conventions:\n" + McpTools.Guide("finance").Text);
        return string.Join("\n\n", parts);
    }
}
