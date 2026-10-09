using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Bento.Api.Services.Ai;

/// <summary>
/// The Claude Messages API through the official SDK. Uses the beta endpoint for
/// server-side refusal fallbacks ("default": the API picks the fallback by the
/// refusal's category); the system prompt and the tool list are cached.
/// </summary>
public sealed class AnthropicAiModel : IAiModel
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly string? _baseUrl;

    /// <summary><c>Backend:Ai:BaseUrl</c> points the client elsewhere (a proxy, or a fake API in tests).</summary>
    public AnthropicAiModel(IConfiguration configuration)
    {
        _baseUrl = configuration["Backend:Ai:BaseUrl"];
    }

    public async Task<AiResponse> SendAsync(string apiKey, AiRequest request, CancellationToken cancellationToken)
    {
        var client = string.IsNullOrWhiteSpace(_baseUrl)
            ? new AnthropicClient { ApiKey = apiKey, Timeout = TimeSpan.FromMinutes(3) }
            : new AnthropicClient { ApiKey = apiKey, Timeout = TimeSpan.FromMinutes(3), BaseUrl = _baseUrl };

        var tools = request.Tools.Select((tool, index) => (BetaToolUnion)new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new()
            {
                Properties = tool.InputSchema["properties"] is { } properties
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(properties.ToJsonString())!
                    : new Dictionary<string, JsonElement>(),
                Required = tool.InputSchema["required"] is { } required
                    ? JsonSerializer.Deserialize<List<string>>(required.ToJsonString())
                    : null,
            },
            // The tool list never changes between calls: cache it with the system prompt.
            CacheControl = index == request.Tools.Count - 1 ? new BetaCacheControlEphemeral() : null,
        }).ToList();

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Betas = [FallbackBeta],
            Fallbacks = new BetaFallbacksParam(new Default()),
            System = new List<BetaTextBlockParam>
            {
                new() { Text = request.System, CacheControl = new BetaCacheControlEphemeral() },
            },
            OutputConfig = new BetaOutputConfig { Effort = request.Effort },
            Tools = tools,
            Messages = request.Messages.Select(ToParam).ToList(),
        };

        BetaMessage response;
        try
        {
            try
            {
                response = await client.Beta.Messages.Create(parameters, cancellationToken);
            }
            catch (AnthropicBadRequestException ex) when (ex.Message.Contains("fallback", StringComparison.OrdinalIgnoreCase))
            {
                // The fallback beta is optional: without it a refusal simply stops.
                response = await client.Beta.Messages.Create(parameters with { Betas = null, Fallbacks = null }, cancellationToken);
            }
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new AiProviderException("Anthropic did not accept the API key; an admin checks it on the AI page.", 422);
        }
        catch (AnthropicRateLimitException)
        {
            throw new AiProviderException("Anthropic is busy right now (rate limit); try again in a moment.", 429);
        }
        catch (AnthropicIOException)
        {
            throw new AiProviderException("Anthropic could not be reached; try again in a moment.", 503);
        }
        catch (AnthropicApiException ex)
        {
            throw new AiProviderException($"Anthropic answered with an error: {ex.Message}");
        }

        var content = new List<AiBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text)) content.Add(new AiText(text.Text));
            else if (block.TryPickThinking(out var thinking)) content.Add(new AiThinking(thinking.Thinking, thinking.Signature));
            else if (block.TryPickRedactedThinking(out var redacted)) content.Add(new AiRedactedThinking(redacted.Data));
            else if (block.TryPickToolUse(out var toolUse))
                content.Add(new AiToolUse(toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input)));
            else if (block.TryPickFallback(out var fallback))
                content.Add(new AiOpaque(new BetaFallbackBlockParam
                {
                    From = new BetaFallbackInfoParam { Model = fallback.From.Model },
                    To = new BetaFallbackInfoParam { Model = fallback.To.Model },
                }));
        }

        var usage = response.Usage;
        return new AiResponse(
            content,
            response.StopReason?.Raw() ?? "end_turn",
            new AiUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0),
            response.Model.Raw());
    }

    private static BetaMessageParam ToParam(AiMessage message)
    {
        var blocks = new List<BetaContentBlockParam>();
        foreach (var block in message.Blocks)
        {
            switch (block)
            {
                case AiText text:
                    blocks.Add(new BetaTextBlockParam { Text = text.Text });
                    break;
                case AiThinking thinking:
                    blocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    break;
                case AiRedactedThinking redacted:
                    blocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    break;
                case AiToolUse toolUse:
                    blocks.Add(new BetaToolUseBlockParam
                    {
                        ID = toolUse.Id,
                        Name = toolUse.Name,
                        Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toolUse.Input.GetRawText())!,
                    });
                    break;
                case AiToolResult result:
                    blocks.Add(new BetaToolResultBlockParam { ToolUseID = result.ToolUseId, Content = result.Content, IsError = result.IsError });
                    break;
                case AiOpaque opaque when opaque.Param is BetaFallbackBlockParam fallback:
                    blocks.Add(fallback);
                    break;
            }
        }
        return new BetaMessageParam { Role = message.Role == "assistant" ? Role.Assistant : Role.User, Content = blocks };
    }
}
