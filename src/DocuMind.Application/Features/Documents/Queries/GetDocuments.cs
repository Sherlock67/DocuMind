using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Documents.Queries;

public enum DocumentSort
{
    UploadedAt,
    Name,
    Size,
    Status,
}

/// <summary>
/// Lists documents with filters that combine (AND). <see cref="FolderId"/> lists one folder
/// (plus its subfolders when <see cref="IncludeSubfolders"/>); <see cref="RootOnly"/> lists only
/// documents at the project root, which is what a file browser shows at the top level.
/// </summary>
public sealed record GetDocumentsQuery(
    Guid? ProjectId = null,
    Guid? FolderId = null,
    bool IncludeSubfolders = false,
    bool RootOnly = false,
    Guid? CollectionId = null,
    DocumentStatus? Status = null,
    string? Search = null,
    DocumentSort SortBy = DocumentSort.UploadedAt,
    bool Descending = true,
    int Page = 1,
    int PageSize = 20) : IQuery<PagedResult<DocumentDto>>;

internal sealed class GetDocumentsQueryValidator : AbstractValidator<GetDocumentsQuery>
{
    public GetDocumentsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(200);
        RuleFor(x => x.RootOnly).Must(r => !r).When(x => x.ProjectId is null).WithMessage("rootOnly requires projectId.");
        RuleFor(x => x.FolderId).Null().When(x => x.RootOnly).WithMessage("Use either folderId or rootOnly, not both.");
        RuleFor(x => x.IncludeSubfolders).Must(i => !i).When(x => x.FolderId is null).WithMessage("includeSubfolders requires folderId.");
    }
}

/// <summary>Only documents the caller can read are ever listed (project membership or a collection share).</summary>
internal sealed class GetDocumentsQueryHandler(IReadDbContext db, IAccessControl access) : IQueryHandler<GetDocumentsQuery, PagedResult<DocumentDto>>
{
    public async Task<PagedResult<DocumentDto>> Handle(GetDocumentsQuery query, CancellationToken ct)
    {
        var documents = db.ReadableDocuments(access.Principal);

        if (query.ProjectId is { } projectId)
            documents = documents.Where(d => d.ProjectId == projectId);

        if (query.RootOnly)
            documents = documents.Where(d => d.FolderId == null);

        if (query.FolderId is { } folderId)
        {
            var folderIds = await ResolveFoldersAsync(folderId, query.IncludeSubfolders, ct);
            documents = documents.Where(d => d.FolderId != null && folderIds.Contains(d.FolderId.Value));
        }

        if (query.CollectionId is { } collectionId)
        {
            await access.RequireCollectionAsync(collectionId, CollectionPermission.View, ct);
            documents = documents.Where(d => db.CollectionItems.Any(i => i.CollectionId == collectionId && i.DocumentId == d.Id));
        }

        if (query.Status is { } status)
            documents = documents.Where(d => d.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            documents = documents.Where(d => d.FileName.ToLower().Contains(search));
        }

        var total = await documents.CountAsync(ct);
        var items = await Sort(documents, query.SortBy, query.Descending)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(DocumentDto.Projection)
            .ToListAsync(ct);

        return new PagedResult<DocumentDto>(items, query.Page, query.PageSize, total);
    }

    private async Task<List<Guid>> ResolveFoldersAsync(Guid folderId, bool includeSubfolders, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == folderId, ct)
            ?? throw new NotFoundException(nameof(Folder), folderId);
        if (!includeSubfolders)
            return [folderId];

        var projectFolders = await db.Folders.Where(f => f.ProjectId == folder.ProjectId).ToListAsync(ct);
        return [.. FolderTree.SelfAndDescendants(folderId, projectFolders)];
    }

    private static IQueryable<Document> Sort(IQueryable<Document> q, DocumentSort sort, bool descending) => (sort, descending) switch
    {
        (DocumentSort.Name, false) => q.OrderBy(d => d.FileName).ThenBy(d => d.Id),
        (DocumentSort.Name, true) => q.OrderByDescending(d => d.FileName).ThenBy(d => d.Id),
        (DocumentSort.Size, false) => q.OrderBy(d => d.SizeBytes).ThenBy(d => d.Id),
        (DocumentSort.Size, true) => q.OrderByDescending(d => d.SizeBytes).ThenBy(d => d.Id),
        (DocumentSort.Status, false) => q.OrderBy(d => d.Status).ThenByDescending(d => d.UploadedAt),
        (DocumentSort.Status, true) => q.OrderByDescending(d => d.Status).ThenByDescending(d => d.UploadedAt),
        (_, false) => q.OrderBy(d => d.UploadedAt).ThenBy(d => d.Id),
        _ => q.OrderByDescending(d => d.UploadedAt).ThenBy(d => d.Id),
    };
}

public sealed record GetDocumentByIdQuery(Guid DocumentId) : IQuery<DocumentDto>;

internal sealed class GetDocumentByIdQueryHandler(IReadDbContext db, IAccessControl access) : IQueryHandler<GetDocumentByIdQuery, DocumentDto>
{
    public async Task<DocumentDto> Handle(GetDocumentByIdQuery query, CancellationToken ct)
    {
        await access.RequireDocumentAsync(query.DocumentId, DocumentPermission.Read, ct);

        return await db.Documents
            .Where(d => d.Id == query.DocumentId)
            .Select(DocumentDto.Projection)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(nameof(Document), query.DocumentId);
    }
}

/// <summary>Status of many documents at once, for polling after a batch upload. Unknown or unreadable ids are omitted.</summary>
public sealed record GetDocumentStatusesQuery(IReadOnlyList<Guid> DocumentIds) : IQuery<IReadOnlyList<DocumentStatusDto>>;

internal sealed class GetDocumentStatusesQueryValidator : AbstractValidator<GetDocumentStatusesQuery>
{
    public GetDocumentStatusesQueryValidator() =>
        RuleFor(x => x.DocumentIds).NotEmpty().Must(ids => ids.Count <= 100).WithMessage("At most 100 ids per request.");
}

internal sealed class GetDocumentStatusesQueryHandler(IReadDbContext db, IAccessControl access)
    : IQueryHandler<GetDocumentStatusesQuery, IReadOnlyList<DocumentStatusDto>>
{
    public async Task<IReadOnlyList<DocumentStatusDto>> Handle(GetDocumentStatusesQuery query, CancellationToken ct) =>
        await db.ReadableDocuments(access.Principal)
            .Where(d => query.DocumentIds.Contains(d.Id))
            .OrderBy(d => d.UploadedAt)
            .Select(d => new DocumentStatusDto(d.Id, d.FileName, d.Status, d.FailureReason, d.ChunkCount, d.UploadedAt, d.ProcessingStartedAt, d.ProcessedAt))
            .ToListAsync(ct);
}
