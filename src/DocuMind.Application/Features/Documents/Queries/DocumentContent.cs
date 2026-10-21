using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Ingestion;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Documents.Queries;

/// <summary>
/// The original file, as a download or (when <see cref="Download"/> is false and the format allows it)
/// for viewing inline in the browser. Every access is audited.
/// </summary>
public sealed record GetDocumentFileQuery(Guid DocumentId, bool Download = true) : IQuery<DocumentFile>;

/// <summary>The caller owns <see cref="Content"/> and must dispose it.</summary>
public sealed record DocumentFile(Stream Content, string FileName, string ContentType, long SizeBytes, bool Inline);

internal sealed class GetDocumentFileQueryHandler(IReadDbContext db, IFileStorage storage, IAccessControl access, IAuditTrail audit)
    : IQueryHandler<GetDocumentFileQuery, DocumentFile>
{
    public async Task<DocumentFile> Handle(GetDocumentFileQuery query, CancellationToken ct)
    {
        var info = await access.RequireDocumentAsync(query.DocumentId, DocumentPermission.Read, ct);

        var doc = await db.Documents
            .Where(d => d.Id == query.DocumentId)
            .Select(d => new { d.FileName, d.StoragePath, d.SizeBytes })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(nameof(Document), query.DocumentId);

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(doc.StoragePath, ct);
        }
        catch (FileNotFoundException)
        {
            throw new NotFoundException("Stored file for document", query.DocumentId);
        }

        var inline = !query.Download && SupportedFileTypes.CanPreviewInline(doc.FileName);
        audit.Record(inline ? AuditActions.DocumentViewed : AuditActions.DocumentDownloaded, "document", query.DocumentId, info.ProjectId,
            new Dictionary<string, string> { ["fileName"] = doc.FileName, ["viaCollectionShare"] = (info.ProjectRole is null).ToString() });

        return new DocumentFile(content, doc.FileName, SupportedFileTypes.ContentTypeFor(doc.FileName), doc.SizeBytes, inline);
    }
}

/// <summary>
/// Extracted text, section by section (one per PDF page). Works for every format, including DOCX,
/// and is only available once the document has been processed.
/// </summary>
public sealed record GetDocumentPreviewQuery(Guid DocumentId, int Page = 1, int PageSize = 5) : IQuery<DocumentPreviewDto>;

public sealed record PreviewSectionDto(int SectionIndex, int? PageNumber, string Text);

public sealed record DocumentPreviewDto(
    Guid DocumentId,
    string FileName,
    DocumentStatus Status,
    int TotalSections,
    int Page,
    int PageSize,
    IReadOnlyList<PreviewSectionDto> Sections);

internal sealed class GetDocumentPreviewQueryValidator : AbstractValidator<GetDocumentPreviewQuery>
{
    public GetDocumentPreviewQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 50);
    }
}

internal sealed class GetDocumentPreviewQueryHandler(IReadDbContext db, IAccessControl access, IAuditTrail audit)
    : IQueryHandler<GetDocumentPreviewQuery, DocumentPreviewDto>
{
    public async Task<DocumentPreviewDto> Handle(GetDocumentPreviewQuery query, CancellationToken ct)
    {
        var info = await access.RequireDocumentAsync(query.DocumentId, DocumentPermission.Read, ct);

        var doc = await db.Documents
            .Where(d => d.Id == query.DocumentId)
            .Select(d => new { d.FileName, d.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(nameof(Document), query.DocumentId);

        var pages = db.DocumentPages.Where(p => p.DocumentId == query.DocumentId);
        var total = await pages.CountAsync(ct);
        var sections = await pages
            .OrderBy(p => p.SectionIndex)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new PreviewSectionDto(p.SectionIndex, p.PageNumber, p.Text))
            .ToListAsync(ct);

        audit.Record(AuditActions.DocumentPreviewed, "document", query.DocumentId, info.ProjectId,
            new Dictionary<string, string> { ["fileName"] = doc.FileName, ["page"] = query.Page.ToString() });

        return new DocumentPreviewDto(query.DocumentId, doc.FileName, doc.Status, total, query.Page, query.PageSize, sections);
    }
}
