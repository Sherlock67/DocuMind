using DocuMind.Domain.Security;
using System.Text;
using DocuMind.Application;
using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Features.Documents.Commands;
using DocuMind.Domain.Documents;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DocuMind.UnitTests.Documents;

public class UploadDocumentsCommandHandlerTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly TestHost _host = new(FakeCurrentUser.As(UserRole.User));
    private readonly IDocumentRepository _documents = Substitute.For<IDocumentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly IIngestionQueue _queue = Substitute.For<IIngestionQueue>();

    public UploadDocumentsCommandHandlerTests()
    {
        _host.Permissions.GetProjectRoleAsync(_host.User.Id, _projectId, Arg.Any<CancellationToken>()).Returns(ProjectRole.Editor);
        _storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new StoredFile($"stored/{ci.Arg<string>()}", $"hash-{ci.Arg<string>()}", 3));
    }

    private ISender CreateSender()
    {
        var services = _host.Services;
        services.AddSingleton(Substitute.For<IFolderRepository>());
        services.AddSingleton(Substitute.For<ICollectionRepository>());
        services.AddSingleton(_documents);
        services.AddSingleton(_unitOfWork);
        services.AddSingleton(_storage);
        services.AddSingleton(_queue);
        return _host.Sender();
    }

    private static UploadFile File(string name, long length = 3) => new(name, length, new MemoryStream(Encoding.UTF8.GetBytes("abc")));

    [Fact]
    public async Task Each_file_in_a_batch_is_accepted_rejected_or_deduplicated_independently()
    {
        var existing = Document.Create(_projectId, null, "old.txt", "text/plain", 3, "p", "hash-dup.txt", Guid.NewGuid(), DateTimeOffset.UtcNow);
        _documents.FindByContentHashAsync(_projectId, "hash-dup.txt", Arg.Any<CancellationToken>()).Returns(existing);

        var result = await CreateSender().Send(new UploadDocumentsCommand(_projectId,
        [
            File("a.pdf"),
            File("evil.html"),
            File("empty.md", length: 0),
            File("dup.txt"),
            File("b.docx"),
        ]));

        Assert.Equal(
            [UploadOutcome.Accepted, UploadOutcome.Rejected, UploadOutcome.Rejected, UploadOutcome.Duplicate, UploadOutcome.Accepted],
            result.Items.Select(i => i.Outcome));
        Assert.Equal(existing.Id, result.Items[3].DocumentId);

        // Rejected files never touch storage; the duplicate's copy is removed again.
        await _storage.DidNotReceive().SaveAsync(Arg.Any<Stream>(), "evil.html", Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteAsync("stored/dup.txt", Arg.Any<CancellationToken>());

        // Accepted files are committed one by one and only then queued.
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _queue.Received(2).EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stored_content_type_comes_from_the_extension_not_the_client()
    {
        Document? added = null;
        _documents.When(d => d.Add(Arg.Any<Document>())).Do(ci => added = ci.Arg<Document>());

        await CreateSender().Send(new UploadDocumentsCommand(_projectId, [File("notes.md")]));

        Assert.Equal("text/markdown; charset=utf-8", added!.ContentType);
        Assert.Equal(_projectId, added.ProjectId);
        Assert.Equal(_host.User.Id, added.UploadedBy);
        Assert.Contains(_host.Audit.Entries, e => e.Action == "document.uploaded" && e.ResourceId == added.Id && e.ActorId == _host.User.Id);
    }

    [Fact]
    public async Task Viewers_cannot_upload()
    {
        _host.Permissions.GetProjectRoleAsync(_host.User.Id, _projectId, Arg.Any<CancellationToken>()).Returns(ProjectRole.Viewer);

        await Assert.ThrowsAsync<ForbiddenException>(() => CreateSender().Send(new UploadDocumentsCommand(_projectId, [File("a.pdf")])));

        await _storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
        Assert.Contains(_host.Audit.Entries, e => e.Action == "access.denied" && !e.Succeeded);
    }

    [Fact]
    public async Task Unknown_project_is_a_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateSender().Send(new UploadDocumentsCommand(Guid.NewGuid(), [File("a.pdf")])));
    }

    [Fact]
    public async Task Batch_limits_are_validated_up_front()
    {
        var tooMany = Enumerable.Range(0, 21).Select(i => File($"{i}.txt")).ToList();

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            CreateSender().Send(new UploadDocumentsCommand(_projectId, tooMany)));

        Assert.Contains(ex.Errors, e => e.ErrorMessage.Contains("At most 20 files"));
        await _storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
    }
}
