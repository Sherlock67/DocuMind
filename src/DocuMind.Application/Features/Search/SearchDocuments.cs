using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Security;
using FluentValidation;

namespace DocuMind.Application.Features.Search;

/// <summary>
/// Retrieval only, no LLM: the best-matching passages, optionally limited to a project,
/// a folder (including subfolders), a collection and/or specific documents.
/// </summary>
public sealed record SearchDocumentsQuery(
    string Query,
    int TopK = 10,
    Guid? ProjectId = null,
    Guid? FolderId = null,
    Guid? CollectionId = null,
    IReadOnlyList<Guid>? DocumentIds = null) : IQuery<IReadOnlyList<SearchHitDto>>;

public sealed record SearchHitDto(
    Guid DocumentId,
    string FileName,
    Guid ChunkId,
    int ChunkIndex,
    int? PageNumber,
    string Content,
    double Score);

internal sealed class SearchDocumentsQueryValidator : AbstractValidator<SearchDocumentsQuery>
{
    public SearchDocumentsQueryValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.TopK).InclusiveBetween(1, 50);
        RuleFor(x => x.DocumentIds).Must(ids => ids is null || ids.Count <= 100);
    }
}

internal sealed class SearchDocumentsQueryHandler(IHybridRetriever retriever, IAccessControl access, IAuditTrail audit)
    : IQueryHandler<SearchDocumentsQuery, IReadOnlyList<SearchHitDto>>
{
    public async Task<IReadOnlyList<SearchHitDto>> Handle(SearchDocumentsQuery query, CancellationToken ct)
    {
        // The SQL access filter already hides anything unreadable; these checks just turn a
        // scope the caller can't see into a clear 404 instead of silently empty results.
        if (query.ProjectId is { } projectId) await access.RequireProjectAsync(projectId, ProjectRole.Viewer, ct);
        if (query.FolderId is { } folderId) await access.RequireFolderAsync(folderId, ProjectRole.Viewer, ct);
        if (query.CollectionId is { } collectionId) await access.RequireCollectionAsync(collectionId, CollectionPermission.View, ct);

        var scope = new SearchScope(query.ProjectId, query.FolderId, query.CollectionId, query.DocumentIds);
        var hits = await retriever.RetrieveAsync(query.Query, query.TopK, scope, access.Principal, ct);

        RetrievalAudit.Record(audit, hits, "search", query.Query);

        return hits
            .Select(h => new SearchHitDto(h.DocumentId, h.FileName, h.ChunkId, h.ChunkIndex, h.PageNumber, h.Content, h.Score))
            .ToList();
    }
}
