using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using DocuMind.Application.Abstractions.AI;
using DocuMind.Domain.Chat;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.AI;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Falls back to the ANTHROPIC_API_KEY environment variable when empty.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    public int MaxTokens { get; set; } = 16000;

    /// <summary>low | medium | high | xhigh | max. Claude Opus 5.5 defaults to medium; grounded Q&amp;A rarely needs more.</summary>
    public string Effort { get; set; } = "medium";
}

/// <summary>
/// Claude via the official Anthropic SDK. Always streams (long answers never hit HTTP timeouts)
/// and opts into server-side refusal fallbacks so a safety decline is retried on a fallback model
/// inside the same call instead of failing the user's question.
/// </summary>
internal sealed class AnthropicChatCompletionService(AnthropicClient client, IOptions<AnthropicOptions> options) : IChatCompletionService
{
    public async IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;
        var parameters = new MessageCreateParams
        {
            Model = o.Model,
            MaxTokens = o.MaxTokens,
            System = request.SystemPrompt,
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(o.Effort) },
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(), // serialises as "default": route by refusal category
            Messages = [.. request.Messages.Select(m => new BetaMessageParam
            {
                Role = m.Role == ChatRole.User ? Role.User : Role.Assistant,
                Content = m.Content,
            })],
        };

        var refused = false;
        await foreach (var evt in client.Beta.Messages.CreateStreaming(parameters, ct))
        {
            if (evt.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var text))
            {
                yield return text.Text;
            }
            else if (evt.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason == BetaStopReason.Refusal)
            {
                refused = true;
            }
        }

        if (refused)
            yield return "\n\n_I can't help with this request._";
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
