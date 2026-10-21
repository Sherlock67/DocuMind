using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DocuMind.Application.Abstractions.AI;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.AI;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>"Voyage" (hosted, Anthropic's recommended embedding partner) or "Ollama" (local).</summary>
    public string Provider { get; set; } = "Voyage";

    public string Model { get; set; } = "voyage-3.5";

    /// <summary>
    /// Must match the vector(N) column in the database. Changing it requires a new migration
    /// and re-processing every document.
    /// </summary>
    public int Dimensions { get; set; } = 1024;

    public string? ApiKey { get; set; }

    public string? BaseUrl { get; set; }
}

internal abstract class EmbeddingServiceBase(IOptions<EmbeddingOptions> options) : ITextEmbeddingService
{
    protected EmbeddingOptions Options { get; } = options.Value;

    public int Dimensions => Options.Dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken ct)
    {
        if (inputs.Count == 0) return [];

        var vectors = await EmbedCoreAsync(inputs, inputType, ct);

        if (vectors.Count != inputs.Count)
            throw new InvalidOperationException($"Embedding provider returned {vectors.Count} vectors for {inputs.Count} inputs.");
        if (vectors.FirstOrDefault(v => v.Length != Dimensions) is { } bad)
            throw new InvalidOperationException(
                $"Embedding model '{Options.Model}' returned {bad.Length} dimensions but the database expects {Dimensions}. " +
                "Fix Embeddings:Dimensions or pick a model with matching output size.");

        return vectors;
    }

    protected abstract Task<IReadOnlyList<float[]>> EmbedCoreAsync(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken ct);
}

/// <summary>Voyage AI embeddings API. https://docs.voyageai.com/reference/embeddings-api</summary>
internal sealed class VoyageEmbeddingService(HttpClient http, IOptions<EmbeddingOptions> options) : EmbeddingServiceBase(options)
{
    protected override async Task<IReadOnlyList<float[]>> EmbedCoreAsync(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken ct)
    {
        var request = new VoyageRequest(
            inputs,
            Options.Model,
            inputType == EmbeddingInputType.Query ? "query" : "document",
            Options.Dimensions);

        using var response = await http.PostAsJsonAsync("v1/embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<VoyageResponse>(ct)
            ?? throw new InvalidOperationException("Empty response from Voyage AI.");

        return body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
    }

    private sealed record VoyageRequest(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input_type")] string InputType,
        [property: JsonPropertyName("output_dimension")] int OutputDimension);

    private sealed record VoyageResponse([property: JsonPropertyName("data")] List<VoyageEmbedding> Data);

    private sealed record VoyageEmbedding(
        [property: JsonPropertyName("embedding")] float[] Embedding,
        [property: JsonPropertyName("index")] int Index);
}

/// <summary>Local embeddings through Ollama, e.g. `ollama pull mxbai-embed-large` (1024 dims).</summary>
internal sealed class OllamaEmbeddingService(HttpClient http, IOptions<EmbeddingOptions> options) : EmbeddingServiceBase(options)
{
    protected override async Task<IReadOnlyList<float[]>> EmbedCoreAsync(IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/embed", new OllamaRequest(Options.Model, inputs), ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OllamaResponse>(ct)
            ?? throw new InvalidOperationException("Empty response from Ollama.");

        return body.Embeddings;
    }

    private sealed record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record OllamaResponse([property: JsonPropertyName("embeddings")] List<float[]> Embeddings);
}
