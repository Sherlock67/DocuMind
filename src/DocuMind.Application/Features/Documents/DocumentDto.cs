using System.Linq.Expressions;
using DocuMind.Domain.Documents;

namespace DocuMind.Application.Features.Documents;

public sealed record DocumentDto(
    Guid Id,
    Guid ProjectId,
    Guid? FolderId,
    string FileName,
    string ContentType,
    long SizeBytes,
    DocumentStatus Status,
    string? FailureReason,
    int PageCount,
    int ChunkCount,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ProcessingStartedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Projection translated to SQL so the read side never materialises entities.</summary>
    internal static readonly Expression<Func<Document, DocumentDto>> Projection = d => new DocumentDto(
        d.Id, d.ProjectId, d.FolderId, d.FileName, d.ContentType, d.SizeBytes, d.Status, d.FailureReason,
        d.PageCount, d.ChunkCount, d.UploadedAt, d.ProcessingStartedAt, d.ProcessedAt, d.UpdatedAt);

    internal static readonly Func<Document, DocumentDto> FromEntity = Projection.Compile();
}

/// <summary>Lightweight shape for polling ingestion progress after an upload.</summary>
public sealed record DocumentStatusDto(
    Guid Id,
    string FileName,
    DocumentStatus Status,
    string? FailureReason,
    int ChunkCount,
    DateTimeOffset UploadedAt,
    DateTimeOffset? ProcessingStartedAt,
    DateTimeOffset? ProcessedAt);
