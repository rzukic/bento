using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bento.Api.Services.Ai;

/// <summary>A tool offered to the model: an MCP tool's name, description and JSON schema.</summary>
public sealed record AiToolSpec(string Name, string Description, JsonObject InputSchema);

/// <summary>A piece of a message, independent of the SDK so the tool loop can be tested without the API.</summary>
public abstract record AiBlock;
public sealed record AiText(string Text) : AiBlock;
/// <summary>Thinking the model returned; sent back unchanged (with its signature) in the same tool loop.</summary>
public sealed record AiThinking(string Thinking, string Signature) : AiBlock;
public sealed record AiRedactedThinking(string Data) : AiBlock;
public sealed record AiToolUse(string Id, string Name, JsonElement Input) : AiBlock;
public sealed record AiToolResult(string ToolUseId, string Content, bool IsError) : AiBlock;
/// <summary>A provider-specific block (e.g. a fallback marker) that must be echoed back as it came.</summary>
public sealed record AiOpaque(object Param) : AiBlock;

public sealed record AiMessage(string Role, IReadOnlyList<AiBlock> Blocks)
{
    public static AiMessage User(string text) => new("user", [new AiText(text)]);
}

public sealed record AiUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static readonly AiUsage Zero = new(0, 0, 0, 0);
    public AiUsage Add(AiUsage other) =>
        new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, CacheReadTokens + other.CacheReadTokens, CacheWriteTokens + other.CacheWriteTokens);
}

/// <param name="System">Stable instructions; cached, so nothing request-specific belongs here.</param>
/// <param name="Effort">low, medium or high.</param>
public sealed record AiRequest(string Model, string System, IReadOnlyList<AiToolSpec> Tools, IReadOnlyList<AiMessage> Messages, int MaxTokens, string Effort);

/// <param name="StopReason">end_turn, tool_use, max_tokens, refusal, …</param>
/// <param name="Model">The model that answered (another one after a fallback).</param>
public sealed record AiResponse(IReadOnlyList<AiBlock> Content, string StopReason, AiUsage Usage, string Model);

/// <summary>One call to the model provider. Implemented by <see cref="AnthropicAiModel"/>; tests fake it.</summary>
public interface IAiModel
{
    Task<AiResponse> SendAsync(string apiKey, AiRequest request, CancellationToken cancellationToken);
}

/// <summary>The provider refused or failed in a way the user should read.</summary>
public sealed class AiProviderException(string message, int status = 502) : Exception(message)
{
    public int Status { get; } = status;
}
