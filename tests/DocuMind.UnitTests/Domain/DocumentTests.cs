using DocuMind.Domain.Common;
using DocuMind.Domain.Documents;

namespace DocuMind.UnitTests.Domain;

public class DocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static Document NewPdf() =>
        Document.Create(Guid.NewGuid(), null, "report.pdf", "application/pdf", 10, "p", "h", Guid.NewGuid(), Now);

    [Fact]
    public void New_documents_are_queued()
    {
        Assert.Equal(DocumentStatus.Queued, NewPdf().Status);
    }

    [Theory]
    [InlineData("Q3 summary", "Q3 summary.pdf")]
    [InlineData("Q3 summary.pdf", "Q3 summary.pdf")]
    [InlineData("  Q3 summary.PDF ", "Q3 summary.PDF")]
    [InlineData("notes.txt", "notes.txt.pdf")]
    public void Rename_keeps_the_original_extension(string newName, string expected)
    {
        var doc = NewPdf();

        doc.Rename(newName, Now.AddMinutes(1));

        Assert.Equal(expected, doc.FileName);
        Assert.Equal(Now.AddMinutes(1), doc.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("what?")]
    public void Rename_rejects_invalid_names(string newName)
    {
        Assert.Throws<DomainException>(() => NewPdf().Rename(newName, Now));
    }

    [Fact]
    public void Lifecycle_goes_queued_processing_completed_and_can_be_requeued()
    {
        var doc = NewPdf();

        doc.MarkProcessing(Now);
        Assert.Equal(DocumentStatus.Processing, doc.Status);
        Assert.Equal(Now, doc.ProcessingStartedAt);

        doc.CompleteIngestion(
            [DocumentChunk.Create(doc.Id, 0, "text", 1, [1f], new Dictionary<string, int> { ["text"] = 1 })],
            [DocumentPage.Create(doc.Id, 0, 1, "text")],
            pageCount: 1, Now.AddSeconds(5));
        Assert.Equal(DocumentStatus.Completed, doc.Status);

        doc.Requeue(Now.AddMinutes(1));
        Assert.Equal(DocumentStatus.Queued, doc.Status);
        Assert.Null(doc.ProcessingStartedAt);
    }

    [Fact]
    public void Failing_records_the_reason_and_clears_content()
    {
        var doc = NewPdf();
        doc.MarkProcessing(Now);

        doc.MarkFailed("boom", Now);

        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.Equal("boom", doc.FailureReason);
        Assert.Empty(doc.Chunks);
        Assert.Empty(doc.Pages);
    }

    [Fact]
    public void Queued_documents_cannot_be_requeued()
    {
        Assert.Throws<DomainException>(() => NewPdf().Requeue(Now));
    }
}
