using DocuMind.Application.Abstractions.AI;
using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace DocuMind.Application.Features.Documents.Commands;

/// <summary>
/// Extract -> chunk -> embed + index keywords -> store chunks and preview pages.
/// Sent by the background ingestion worker.
/// </summary>
public sealed record IngestDocumentCommand(Guid DocumentId) : ICommand<Unit>, ISystemRequest;

internal sealed class IngestDocumentCommandHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IFileStorage storage,
    IEnumerable<ITextExtractor> extractors,
    ITextChunker chunker,
    ITextEmbeddingService embeddings,
    IKeywordTokenizer tokenizer,
    TimeProvider clock,
    IAuditTrail audit,
    ILogger<IngestDocumentCommandHandler> logger) : ICommandHandler<IngestDocumentCommand, Unit>
{
    private const int EmbeddingBatchSize = 64;

    public async Task<Unit> Handle(IngestDocumentCommand command, CancellationToken ct)
    {
        var document = await documents.GetForIngestionAsync(command.DocumentId, ct);
        if (document is null || document.Status is DocumentStatus.Completed or DocumentStatus.Failed)
            return Unit.Value; // deleted or already handled (duplicate queue entry)

        document.MarkProcessing(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);

        try
        {
            var extension = Path.GetExtension(document.FileName);
            var extractor = extractors.FirstOrDefault(e => e.CanExtract(extension))
                ?? throw new NotSupportedException($"No text extractor registered for '{extension}'.");

            ExtractedDocument extracted;
            await using (var stream = await storage.OpenReadAsync(document.StoragePath, ct))
            {
                extracted = await extractor.ExtractAsync(stream, ct);
            }

            var chunks = chunker.Chunk(extracted);
            if (chunks.Count == 0)
                throw new InvalidOperationException("The document contains no extractable text (scanned PDFs need OCR first).");

            var vectors = new List<float[]>(chunks.Count);
            foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
            {
                vectors.AddRange(await embeddings.EmbedAsync([.. batch.Select(c => c.Content)], EmbeddingInputType.Document, ct));
            }

            document.CompleteIngestion(
                chunks.Select((c, i) => DocumentChunk.Create(
                    document.Id, c.Index, c.Content, c.PageNumber, vectors[i], tokenizer.TermFrequencies(c.Content))),
                extracted.Pages.Select((p, i) => DocumentPage.Create(document.Id, i, p.PageNumber, p.Text)),
                extracted.PageCount,
                clock.GetUtcNow());

            logger.LogInformation("Ingested {DocumentId} ({FileName}): {Chunks} chunks", document.Id, document.FileName, chunks.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Ingestion failed for {DocumentId}", document.Id);
            document.MarkFailed(ex.Message, clock.GetUtcNow());
        }

        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(
            document.Status == DocumentStatus.Completed ? AuditActions.DocumentProcessed : AuditActions.DocumentProcessingFailed,
            "document", document.Id, document.ProjectId,
            succeeded: document.Status == DocumentStatus.Completed,
            details: new Dictionary<string, string>
            {
                ["fileName"] = document.FileName,
                ["chunks"] = document.ChunkCount.ToString(),
                ["reason"] = document.FailureReason ?? "",
            });
        return Unit.Value;
    }
}
