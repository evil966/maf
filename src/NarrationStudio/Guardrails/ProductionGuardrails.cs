using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NarrationStudio.Guardrails;

public sealed class ProductionGuardrails
{
    private static readonly string[] BlockedTerms =
    [
        "harmful", "illegal", "violence", "exploit", "malware", "bypass paywall"
    ];

    private readonly ILogger _logger;

    public ProductionGuardrails(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var filtered = messages.Select(FilterMessage).ToList();
        if (filtered.Any(m => m.Text.Contains("[BLOCKED", StringComparison.Ordinal)))
        {
            _logger.LogWarning("Guardrail blocked an inbound request.");
            return new AgentResponse([
                new ChatMessage(ChatRole.Assistant,
                    "This request is outside the narration-to-video production guardrails. Stay on transcript segmentation, slides, TTS, and video assembly.")
            ]);
        }

        var response = await innerAgent.RunAsync(filtered, session, options, cancellationToken);
        response.Messages = response.Messages.Select(FilterMessage).ToList();
        return response;
    }

    public async ValueTask<object?> FunctionAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tool {Name} starting", context.Function.Name);
        var result = await next(context, cancellationToken);
        _logger.LogInformation("Tool {Name} finished", context.Function.Name);
        return result;
    }

    private ChatMessage FilterMessage(ChatMessage message)
    {
        var text = message.Text ?? "";
        foreach (var term in BlockedTerms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                message.Role == ChatRole.User)
            {
                return new ChatMessage(message.Role, "[BLOCKED: content outside production policy]");
            }
        }

        var redacted = Regex.Replace(text, @"\b[\w\.-]+@[\w\.-]+\.\w+\b", "[REDACTED-EMAIL]");
        redacted = Regex.Replace(redacted, @"\b\d{3}-\d{3}-\d{4}\b", "[REDACTED-PHONE]");
        return new ChatMessage(message.Role, redacted);
    }
}
