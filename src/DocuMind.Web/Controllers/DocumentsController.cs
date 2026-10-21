using DocuMind.Web.Api;
using DocuMind.Web.Infrastructure;
using DocuMind.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace DocuMind.Web.Controllers;

public sealed class DocumentsController(DocuMindApiClient api) : AppController
{
    /// <summary>Batch upload: forwards the files to the API, then shows each file's outcome with live processing status.</summary>
    [HttpPost("/projects/{projectId:guid}/upload")]
    public async Task<IActionResult> Upload(Guid projectId, Guid? folderId, Guid? collectionId, string? destination, IFormFileCollection files)
    {
        if (files.Count == 0)
        {
            Error("Choose at least one file to upload.");
            return Back(Urls.ReturnUrlFrom(HttpContext), $"/projects/{projectId}");
        }

        var result = await api.UploadAsync(projectId, folderId, collectionId, files);
        var project = await api.ProjectAsync(projectId);
        return View("UploadResult", new UploadResultViewModel(project, destination ?? "Project root", result));
    }

    /// <summary>JSON for the upload page's status polling.</summary>
    [HttpGet("/documents/status")]
    public async Task<IActionResult> Status([FromQuery] Guid[] ids) =>
        ids.Length == 0 ? Json(Array.Empty<object>()) : Json(await api.StatusesAsync(ids.Take(100)));

    [HttpGet("/documents/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, int page = 1)
    {
        var document = await api.DocumentAsync(id);
        var project = await api.ProjectAsync(document.ProjectId);
        var access = AccessFor(project);

        var preview = document.Status == Models.DocumentStatus.Completed ? await api.PreviewAsync(id, Math.Max(1, page)) : null;
        var folders = FolderTree.Flatten(await api.FolderTreeAsync(project.Id));
        var collections = (await api.CollectionsAsync(project.Id)).Where(c => access.CanManage(c, MyId)).ToList();

        return View(new DocumentDetailsViewModel(document, project, access, preview, folders, collections));
    }

    [HttpGet("/documents/{id:guid}/download")]
    public Task<IActionResult> Download(Guid id) => StreamAsync(id, download: true);

    /// <summary>Inline view for the document page's viewer (PDF, TXT, MD).</summary>
    [HttpGet("/documents/{id:guid}/inline")]
    public Task<IActionResult> Inline(Guid id) => StreamAsync(id, download: false);

    [HttpPost("/documents/{id:guid}/rename")]
    public async Task<IActionResult> Rename(Guid id, string fileName, string? returnUrl)
    {
        await api.RenameDocumentAsync(id, fileName.Trim());
        Success("Document renamed.");
        return Back(returnUrl);
    }

    [HttpPost("/documents/{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, Guid? folderId, string? returnUrl)
    {
        await api.MoveDocumentsAsync([id], folderId);
        Success("Document moved.");
        return Back(returnUrl);
    }

    [HttpPost("/documents/{id:guid}/reprocess")]
    public async Task<IActionResult> Reprocess(Guid id, string? returnUrl)
    {
        await api.ReprocessDocumentAsync(id);
        Success("Queued for processing again.");
        return Back(returnUrl);
    }

    [HttpPost("/documents/{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, Guid projectId, string? returnUrl)
    {
        await api.DeleteDocumentAsync(id);
        Success("Document deleted.");
        // The document page itself no longer exists: go back to the project instead.
        var back = Urls.Safe(returnUrl, $"/projects/{projectId}");
        return LocalRedirect(back.StartsWith($"/documents/{id}") ? $"/projects/{projectId}" : back);
    }

    /// <summary>
    /// Proxies the file from the API (the browser can't call the API itself: it has no API token).
    /// Inline responses get the same hardening as the API: nosniff, and a sandboxing CSP for text.
    /// </summary>
    private async Task<IActionResult> StreamAsync(Guid id, bool download)
    {
        var response = await api.ContentAsync(id, download);
        HttpContext.Response.RegisterForDispose(response);

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"') ?? "document";
        var stream = await response.Content.ReadAsStreamAsync();

        if (download || disposition?.DispositionType == "attachment")
            return File(stream, contentType, fileName, enableRangeProcessing: true);

        // Text formats render as sandboxed documents (no scripts, no same-origin access). PDFs are left
        // unsandboxed because browsers refuse to run their built-in PDF viewer in a sandboxed document;
        // the server-chosen application/pdf type plus nosniff means it can only ever be shown as a PDF.
        if (!contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase))
            Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; style-src 'unsafe-inline'";
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = fileName }.ToString();
        return File(stream, contentType, enableRangeProcessing: true);
    }
}
