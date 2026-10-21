using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Features.Documents.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Documents processed concurrently. Bounded mainly by the embedding API's rate limit.</summary>
    public int MaxConcurrency { get; set; } = 2;
}

/// <summary>
/// In-process queue. Durable state lives in the documents table (status = Queued), so the
/// queue itself can be lossy; swap for RabbitMQ / Azure Service Bus to scale workers out.
/// </summary>
internal sealed class ChannelIngestionQueue : IIngestionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false });

    public ValueTask EnqueueAsync(Guid documentId, CancellationToken ct) => _channel.Writer.WriteAsync(documentId, ct);

    public async IAsyncEnumerable<Guid> DequeueAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(ct))
            yield return id;
    }
}

/// <summary>Consumes the ingestion queue and dispatches an <see cref="IngestDocumentCommand"/> per document.</summary>
internal sealed class IngestionWorker(
    IServiceScopeFactory scopeFactory,
    IIngestionQueue queue,
    IOptions<IngestionOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrency),
            CancellationToken = stoppingToken,
        };

        try
        {
            await Parallel.ForEachAsync(queue.DequeueAllAsync(stoppingToken), parallel, async (documentId, ct) =>
            {
                // The same id can be queued twice (upload + startup recovery); never ingest it concurrently.
                if (!_inFlight.TryAdd(documentId, 0))
                    return;

                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ISender>().Send(new IngestDocumentCommand(documentId), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The handler records domain failures itself; this only catches infrastructure faults.
                    logger.LogError(ex, "Ingestion worker failed on document {DocumentId}", documentId);
                }
                finally
                {
                    _inFlight.TryRemove(documentId, out _);
                }
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Recovers documents left Queued/Processing by a restart or crash.</summary>
    private async Task RequeueUnfinishedAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var ids = await scope.ServiceProvider.GetRequiredService<IDocumentRepository>().GetUnfinishedIdsAsync(ct);
            foreach (var id in ids)
                await queue.EnqueueAsync(id, ct);

            if (ids.Count > 0)
                logger.LogInformation("Re-queued {Count} unfinished documents", ids.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not re-queue unfinished documents on startup");
        }
    }
}
