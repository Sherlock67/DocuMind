using DocuMind.Domain.Chat;

namespace DocuMind.Application.Abstractions.AI;

/// <summary>Retrieval models embed documents and queries differently; providers that care get told which.</summary>
public enum EmbeddingInputType
{
    Document,
    Query,
}

public interface ITextEmbeddingService
{
    int Dimensions { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken ct);
}

public sealed record ChatTurn(ChatRole Role, string Content);

public sealed record ChatCompletionRequest(string SystemPrompt, IReadOnlyList<ChatTurn> Messages);

/// <summary>LLM that writes the grounded answer. Implementations must stream.</summary>
public interface IChatCompletionService
{
    IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request, CancellationToken ct);
}
