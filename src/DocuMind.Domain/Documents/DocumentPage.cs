using DocuMind.Domain.Common;

namespace DocuMind.Domain.Documents;

/// <summary>
/// Extracted text of one section of a document, kept for previewing any format in the browser
/// (including DOCX, which browsers cannot render) and for showing a citation in context.
/// PDFs produce one section per page; formats without pages produce a single section.
/// </summary>
public sealed class DocumentPage : Entity
{
    private DocumentPage() { } // EF Core

    public Guid DocumentId { get; private set; }

    /// <summary>0-based position within the document.</summary>
    public int SectionIndex { get; private set; }

    /// <summary>1-based physical page number when the format has pages.</summary>
    public int? PageNumber { get; private set; }

    public string Text { get; private set; } = null!;

    public static DocumentPage Create(Guid documentId, int sectionIndex, int? pageNumber, string text) => new()
    {
        DocumentId = documentId,
        SectionIndex = sectionIndex,
        PageNumber = pageNumber,
        Text = text,
    };
}
