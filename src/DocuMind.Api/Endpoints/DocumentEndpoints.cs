using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Features.Documents;
using DocuMind.Application.Features.Documents.Commands;
using DocuMind.Application.Features.Documents.Queries;
using DocuMind.Domain.Documents;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace DocuMind.Api.Endpoints;

internal static class DocumentEndpoints
{
    public sealed record RenameRequest(string FileName);

    public sealed record MoveRequest(IReadOnlyList<Guid> DocumentIds, Guid? FolderId);

    public static RouteGroupBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Documents");

        group.MapPost("/", Upload)
            .DisableAntiforgery() // API clients authenticate per request; there is no cookie session to forge
            .WithSummary("Upload one or more files (multipart field 'files') into a project, optionally into a folder and collection.")
            .WithDescription("Each file is accepted, reported as a duplicate, or rejected on its own. Processing runs in the background; poll /api/documents/status.");

        group.MapGet("/", List).WithSummary("List and filter documents.");
        group.MapGet("/status", Statuses).WithSummary("Processing status for many documents at once (repeat ?ids=).");
        group.MapGet("/{id:guid}", GetById).WithName("GetDocument").WithSummary("Get a document's metadata and status.");
        group.MapGet("/{id:guid}/content", Content)
            .WithSummary("Download the original file, or view it inline (?download=false) for PDF, TXT and MD.");
        group.MapGet("/{id:guid}/preview", Preview).WithSummary("Extracted text by page/section; works for every format.");
        group.MapPut("/{id:guid}/name", Rename).WithSummary("Rename a document (the extension is kept).");
        group.MapPost("/move", Move).WithSummary("Move documents into a folder, or to the project root with folderId = null.");
        group.MapDelete("/{id:guid}", Delete).WithSummary("Delete a document, its chunks and the stored file.");
        group.MapPost("/{id:guid}/reprocess", Reprocess).WithSummary("Re-run extraction, chunking and embedding.");

        return group;
    }

    private static async Task<Results<Accepted<BatchUploadResult>, Ok<BatchUploadResult>>> Upload(
        [FromForm] Guid projectId,
        [FromForm] Guid? folderId,
        [FromForm] Guid? collectionId,
        IFormFileCollection files,
        ISender sender,
        CancellationToken ct)
    {
        var streams = new List<Stream>(files.Count);
        try
        {
            var uploads = files.Select(f =>
            {
                var stream = f.OpenReadStream();
                streams.Add(stream);
                return new UploadFile(f.FileName, f.Length, stream);
            }).ToList();

            var result = await sender.Send(new UploadDocumentsCommand(projectId, uploads, folderId, collectionId), ct);

            // 202 when anything new was queued for processing; 200 when every file was a duplicate or rejected.
            return result.Accepted > 0 ? TypedResults.Accepted("/api/documents/status", result) : TypedResults.Ok(result);
        }
        finally
        {
            foreach (var stream in streams)
                await stream.DisposeAsync();
        }
    }

    private static async Task<Ok<PagedResult<DocumentDto>>> List(
        ISender sender,
        CancellationToken ct,
        Guid? projectId = null,
        Guid? folderId = null,
        bool includeSubfolders = false,
        bool rootOnly = false,
        Guid? collectionId = null,
        DocumentStatus? status = null,
        string? search = null,
        DocumentSort sortBy = DocumentSort.UploadedAt,
        bool descending = true,
        int page = 1,
        int pageSize = 20) =>
        TypedResults.Ok(await sender.Send(
            new GetDocumentsQuery(projectId, folderId, includeSubfolders, rootOnly, collectionId, status, search, sortBy, descending, page, pageSize), ct));

    private static async Task<Ok<IReadOnlyList<DocumentStatusDto>>> Statuses([FromQuery] Guid[] ids, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetDocumentStatusesQuery(ids), ct));

    private static async Task<Ok<DocumentDto>> GetById(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetDocumentByIdQuery(id), ct));

    private static async Task<IResult> Content(Guid id, ISender sender, HttpContext http, CancellationToken ct, bool download = true)
    {
        var file = await sender.Send(new GetDocumentFileQuery(id, download), ct);
        var inline = file.Inline;

        // Never let the browser guess a type, and sandbox anything rendered inline (no scripts, no same-origin).
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        if (inline)
        {
            http.Response.Headers[HeaderNames.ContentSecurityPolicy] = "sandbox; default-src 'none'; style-src 'unsafe-inline'";
            http.Response.Headers[HeaderNames.ContentDisposition] =
                new ContentDispositionHeaderValue("inline") { FileNameStar = file.FileName }.ToString();
        }

        return TypedResults.Stream(
            file.Content,
            file.ContentType,
            fileDownloadName: inline ? null : file.FileName,
            enableRangeProcessing: true);
    }

    private static async Task<Ok<DocumentPreviewDto>> Preview(Guid id, ISender sender, CancellationToken ct, int page = 1, int pageSize = 5) =>
        TypedResults.Ok(await sender.Send(new GetDocumentPreviewQuery(id, page, pageSize), ct));

    private static async Task<Ok<DocumentDto>> Rename(Guid id, RenameRequest request, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new RenameDocumentCommand(id, request.FileName), ct));

    private static async Task<NoContent> Move(MoveRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new MoveDocumentsCommand(request.DocumentIds ?? [], request.FolderId), ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Delete(Guid id, ISender sender, CancellationToken ct)
    {
        await sender.Send(new DeleteDocumentCommand(id), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Accepted> Reprocess(Guid id, ISender sender, CancellationToken ct)
    {
        await sender.Send(new ReprocessDocumentCommand(id), ct);
        return TypedResults.Accepted($"/api/documents/{id}");
    }
}
