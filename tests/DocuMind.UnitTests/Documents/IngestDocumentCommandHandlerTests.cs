using DocuMind.Application;
using DocuMind.Application.Abstractions.AI;
using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Features.Documents.Commands;
using DocuMind.Domain.Documents;
using DocuMind.Infrastructure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DocuMind.UnitTests.Documents;

public class IngestDocumentCommandHandlerTests
{
    private readonly Document _document = Document.Create(Guid.NewGuid(), null, "guide.txt", "text/plain", 42, "2026/10/x.txt", "hash", Guid.NewGuid(), DateTimeOffset.UtcNow);
    private readonly IDocumentRepository _documents = Substitute.For<IDocumentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly ITextExtractor _extractor = Substitute.For<ITextExtractor>();
    private readonly ITextChunker _chunker = Substitute.For<ITextChunker>();
    private readonly ITextEmbeddingService _embeddings = Substitute.For<ITextEmbeddingService>();

    public IngestDocumentCommandHandlerTests()
    {
        _documents.GetForIngestionAsync(_document.Id, Arg.Any<CancellationToken>()).Returns(_document);
        _storage.OpenReadAsync(_document.StoragePath, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream());
        _extractor.CanExtract(".txt").Returns(true);
        _extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument([new ExtractedPage(null, "some text")]));
    }

    private readonly TestHost _host = new(FakeCurrentUser.System());

    private ISender CreateSender()
    {
        var services = _host.Services;
        services.AddSingleton(_documents);
        services.AddSingleton(_unitOfWork);
        services.AddSingleton(_storage);
        services.AddSingleton(_extractor);
        services.AddSingleton(_chunker);
        services.AddSingleton(_embeddings);
        services.AddSingleton<IKeywordTokenizer, KeywordTokenizer>();
        return _host.Sender();
    }

    [Fact]
    public async Task Successful_ingestion_stores_embedded_chunks_and_marks_ready()
    {
        _chunker.Chunk(Arg.Any<ExtractedDocument>()).Returns([new TextChunk(0, "chunk a", null), new TextChunk(1, "chunk b", null)]);
        _embeddings.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), EmbeddingInputType.Document, Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyList<string>>().Select(_ => new[] { 0.1f, 0.2f }).ToList());

        await CreateSender().Send(new IngestDocumentCommand(_document.Id));

        Assert.Equal(DocumentStatus.Completed, _document.Status);
        Assert.Equal(2, _document.ChunkCount);
        Assert.Equal("some text", Assert.Single(_document.Pages).Text);
        Assert.NotNull(_document.ProcessingStartedAt);
        Assert.Equal(["chunk a", "chunk b"], _document.Chunks.Select(c => c.Content));
        Assert.All(_document.Chunks, c => Assert.Equal("chunk", Assert.Single(c.Terms).Term)); // keyword index built
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());

        var entry = Assert.Single(_host.Audit.Entries);
        Assert.Equal("document.processed", entry.Action);
        Assert.Equal("system", entry.ActorEmail);
    }

    [Fact]
    public async Task Ingestion_cannot_be_triggered_by_a_signed_in_user()
    {
        var host = new TestHost(FakeCurrentUser.As(DocuMind.Domain.Security.UserRole.Admin));

        await Assert.ThrowsAsync<DocuMind.Application.Common.Exceptions.ForbiddenException>(() =>
            host.Sender().Send(new IngestDocumentCommand(_document.Id)));
    }

    [Fact]
    public async Task Document_without_text_is_marked_failed_with_a_reason()
    {
        _chunker.Chunk(Arg.Any<ExtractedDocument>()).Returns([]);

        await CreateSender().Send(new IngestDocumentCommand(_document.Id));

        Assert.Equal(DocumentStatus.Failed, _document.Status);
        Assert.Contains("no extractable text", _document.FailureReason);
        await _embeddings.DidNotReceiveWithAnyArgs().EmbedAsync(default!, default, default);
    }

    [Fact]
    public async Task Already_completed_documents_are_skipped()
    {
        _chunker.Chunk(Arg.Any<ExtractedDocument>()).Returns([new TextChunk(0, "chunk", null)]);
        _embeddings.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbeddingInputType>(), Arg.Any<CancellationToken>())
            .Returns([[1f]]);
        var sender = CreateSender();
        await sender.Send(new IngestDocumentCommand(_document.Id));
        _unitOfWork.ClearReceivedCalls();

        await sender.Send(new IngestDocumentCommand(_document.Id));

        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }
}
