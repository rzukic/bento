using System.Diagnostics;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Services.Ai;

/// <summary>
/// Bento's one AI layer (Step 4.7): a tool loop over the MCP tools, run as the
/// signed-in user, so every feature (explain, later categorise and describe)
/// sees the data exactly as that user may and nothing else. Each run is
/// recorded with its tokens and cost and checked against the monthly cap first.
/// </summary>
public sealed class AiAgent
{
    /// <summary>Model turns per run; the last one is asked to answer with what it has.</summary>
    private const int MaxTurns = 8;
    private const int MaxTokens = 16000;
    private const string Effort = "medium";

    private readonly IAiModel _model;
    private readonly McpTools _tools;
    private readonly AiSettingsService _settings;
    private readonly ILogger<AiAgent> _logger;

    public AiAgent(IAiModel model, McpTools tools, AiSettingsService settings, ILogger<AiAgent> logger)
    {
        _model = model;
        _tools = tools;
        _settings = settings;
        _logger = logger;
    }

    public sealed record AgentResult(string Text, string Model, AiUsage Usage, decimal CostUsd, int ToolCalls, int DurationMs);

    /// <param name="feature">For the usage log, e.g. "explain".</param>
    /// <param name="toolNames">The tools this feature may use; the caller's permissions narrow them further.</param>
    public async Task<AgentResult> RunAsync(string feature, McpCaller caller, string system, string userMessage, IReadOnlyCollection<string> toolNames, CancellationToken ct)
    {
        var (apiKey, settings) = await _settings.RequireReadyAsync();
        var tools = _tools.All
            .Where(tool => toolNames.Contains(tool.Name) && caller.Allows(tool.Permission))
            .ToList();
        var specs = tools.Select(tool => new AiToolSpec(tool.Name, tool.Description, tool.InputSchema)).ToList();

        var messages = new List<AiMessage> { AiMessage.User(userMessage) };
        var usage = AiUsage.Zero;
        var model = settings.Model;
        var toolCalls = 0;
        var watch = Stopwatch.StartNew();
        string? error = null;

        try
        {
            for (var turn = 1; ; turn++)
            {
                var response = await _model.SendAsync(apiKey, new AiRequest(settings.Model, system, specs, messages, MaxTokens, Effort), ct);
                usage = usage.Add(response.Usage);
                model = response.Model;

                if (response.StopReason == "refusal")
                    throw new AiProviderException("The AI declined to answer this.", 422);

                messages.Add(new AiMessage("assistant", response.Content));
                var uses = response.Content.OfType<AiToolUse>().ToList();
                if (response.StopReason != "tool_use" || uses.Count == 0)
                {
                    var text = string.Join("\n\n", response.Content.OfType<AiText>().Select(t => t.Text.Trim()).Where(t => t.Length > 0));
                    if (text.Length == 0)
                        throw new AiProviderException(response.StopReason == "max_tokens" ? "The answer was cut off." : "The AI gave no answer.");
                    return new AgentResult(text, model, usage, AiSettingsService.Cost(model, usage), toolCalls, (int)watch.ElapsedMilliseconds);
                }

                if (turn >= MaxTurns)
                    throw new AiProviderException("The AI needed too many steps; try a narrower question.");

                // Every tool_use gets its result in one user message.
                var results = new List<AiBlock>();
                foreach (var use in uses)
                {
                    toolCalls++;
                    var tool = tools.FirstOrDefault(t => t.Name == use.Name);
                    McpToolResult result;
                    if (tool is null)
                    {
                        result = new McpToolResult($"There is no tool '{use.Name}' here.", true);
                    }
                    else
                    {
                        try
                        {
                            result = await tool.Run(caller, use.Input, ct);
                        }
                        catch (McpArgumentException ex)
                        {
                            result = new McpToolResult(ex.Message, true);
                        }
                    }
                    results.Add(new AiToolResult(use.Id, result.Text, result.IsError));
                }
                if (turn == MaxTurns - 1)
                    results.Add(new AiText("That was the last tool call you get: answer now with what you have."));
                messages.Add(new AiMessage("user", results));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            try
            {
                await _settings.RecordAsync(new AiSettingsService.UsageEntry(caller.UserId, feature, model, usage, toolCalls, error is null, error, (int)watch.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record the AI call for {Feature}", feature);
            }
        }
    }
}
