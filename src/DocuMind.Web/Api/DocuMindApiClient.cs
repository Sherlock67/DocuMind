using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocuMind.Web.Auth;
using DocuMind.Web.Models;
using Microsoft.AspNetCore.Authentication;

namespace DocuMind.Web.Api;

/// <summary>Adds the signed-in user's API access token and real client IP/user agent to every API call.</summary>
public sealed class ApiAuthHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (accessor.HttpContext is { } http)
        {
            if (await http.GetTokenAsync(TokenNames.Access) is { Length: > 0 } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            ApiSession.Forward(request, http);
        }

        return await base.SendAsync(request, ct);
    }
}

/// <summary>Typed client for the DocuMind REST API. Any non-success response becomes an <see cref="ApiException"/>.</summary>
public sealed class DocuMindApiClient(HttpClient http, JsonSerializerOptions json)
{
    // ----- Account -----------------------------------------------------------------------------

    public Task<UserDto> MeAsync() => GetAsync<UserDto>("api/auth/me");

    public Task ChangePasswordAsync(string currentPassword, string newPassword) =>
        SendAsync(HttpMethod.Post, "api/auth/change-password", new { currentPassword, newPassword });

    // ----- Projects & members ------------------------------------------------------------------

    public Task<PagedResult<ProjectDto>> ProjectsAsync(int page = 1, int pageSize = 60, string? search = null) =>
        GetAsync<PagedResult<ProjectDto>>("api/projects" + Query(("page", page), ("pageSize", pageSize), ("search", search)));

    public Task<ProjectDto> ProjectAsync(Guid id) => GetAsync<ProjectDto>($"api/projects/{id}");

    public Task<ProjectDto> CreateProjectAsync(string name, string? description) =>
        SendAsync<ProjectDto>(HttpMethod.Post, "api/projects", new { name, description });

    public Task UpdateProjectAsync(Guid id, string name, string? description) =>
        SendAsync(HttpMethod.Put, $"api/projects/{id}", new { name, description });

    public Task DeleteProjectAsync(Guid id, bool force) => SendAsync(HttpMethod.Delete, $"api/projects/{id}" + Query(("force", force)));

    public Task<IReadOnlyList<ProjectMemberDto>> MembersAsync(Guid projectId) =>
        GetAsync<IReadOnlyList<ProjectMemberDto>>($"api/projects/{projectId}/members");

    public Task AddMemberAsync(Guid projectId, string email, ProjectRole role) =>
        SendAsync(HttpMethod.Post, $"api/projects/{projectId}/members", new { email, role });

    public Task ChangeMemberRoleAsync(Guid projectId, Guid userId, ProjectRole role) =>
        SendAsync(HttpMethod.Put, $"api/projects/{projectId}/members/{userId}", new { role });

    public Task RemoveMemberAsync(Guid projectId, Guid userId) => SendAsync(HttpMethod.Delete, $"api/projects/{projectId}/members/{userId}");

    // ----- Folders -----------------------------------------------------------------------------

    public Task<IReadOnlyList<FolderTreeNode>> FolderTreeAsync(Guid projectId) =>
        GetAsync<IReadOnlyList<FolderTreeNode>>($"api/projects/{projectId}/folders");

    public Task CreateFolderAsync(Guid projectId, string name, Guid? parentId) =>
        SendAsync(HttpMethod.Post, $"api/projects/{projectId}/folders", new { name, parentId });

    public Task RenameFolderAsync(Guid folderId, string name) => SendAsync(HttpMethod.Put, $"api/folders/{folderId}/name", new { name });

    public Task MoveFolderAsync(Guid folderId, Guid? parentId) => SendAsync(HttpMethod.Put, $"api/folders/{folderId}/parent", new { parentId });

    public Task DeleteFolderAsync(Guid folderId, bool recursive) => SendAsync(HttpMethod.Delete, $"api/folders/{folderId}" + Query(("recursive", recursive)));

    // ----- Collections & sharing ---------------------------------------------------------------

    public Task<IReadOnlyList<CollectionDto>> CollectionsAsync(Guid projectId) =>
        GetAsync<IReadOnlyList<CollectionDto>>($"api/projects/{projectId}/collections");

    public Task<CollectionDto> CollectionAsync(Guid id) => GetAsync<CollectionDto>($"api/collections/{id}");

    public Task CreateCollectionAsync(Guid projectId, string name, string? description, CollectionVisibility visibility) =>
        SendAsync(HttpMethod.Post, $"api/projects/{projectId}/collections", new { name, description, visibility });

    public Task UpdateCollectionAsync(Guid id, string name, string? description, CollectionVisibility visibility) =>
        SendAsync(HttpMethod.Put, $"api/collections/{id}", new { name, description, visibility });

    public Task DeleteCollectionAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/collections/{id}");

    public Task AddToCollectionAsync(Guid collectionId, IReadOnlyList<Guid> documentIds) =>
        SendAsync(HttpMethod.Post, $"api/collections/{collectionId}/documents", new { documentIds });

    public Task RemoveFromCollectionAsync(Guid collectionId, Guid documentId) =>
        SendAsync(HttpMethod.Delete, $"api/collections/{collectionId}/documents/{documentId}");

    public Task<IReadOnlyList<CollectionShareDto>> SharesAsync(Guid collectionId) =>
        GetAsync<IReadOnlyList<CollectionShareDto>>($"api/collections/{collectionId}/shares");

    public Task ShareCollectionAsync(Guid collectionId, string email) =>
        SendAsync(HttpMethod.Post, $"api/collections/{collectionId}/shares", new { email });

    public Task UnshareCollectionAsync(Guid collectionId, Guid userId) => SendAsync(HttpMethod.Delete, $"api/collections/{collectionId}/shares/{userId}");

    // ----- Documents ---------------------------------------------------------------------------

    public Task<PagedResult<DocumentDto>> DocumentsAsync(DocumentQuery q) =>
        GetAsync<PagedResult<DocumentDto>>("api/documents" + Query(
            ("projectId", q.ProjectId), ("folderId", q.FolderId), ("includeSubfolders", q.IncludeSubfolders ? true : null),
            ("rootOnly", q.RootOnly ? true : null), ("collectionId", q.CollectionId), ("status", q.Status),
            ("search", q.Search), ("sortBy", q.SortBy), ("descending", q.Descending), ("page", q.Page), ("pageSize", q.PageSize)));

    public Task<DocumentDto> DocumentAsync(Guid id) => GetAsync<DocumentDto>($"api/documents/{id}");

    public Task<IReadOnlyList<DocumentStatusDto>> StatusesAsync(IEnumerable<Guid> ids) =>
        GetAsync<IReadOnlyList<DocumentStatusDto>>("api/documents/status?" + string.Join("&", ids.Select(id => $"ids={id}")));

    public Task<DocumentPreviewDto> PreviewAsync(Guid id, int page, int pageSize = 5) =>
        GetAsync<DocumentPreviewDto>($"api/documents/{id}/preview" + Query(("page", page), ("pageSize", pageSize)));

    /// <summary>Forwards the browser's files to the API as one multipart request, streaming each file.</summary>
    public async Task<BatchUploadResult> UploadAsync(Guid projectId, Guid? folderId, Guid? collectionId, IFormFileCollection files)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(projectId.ToString()), "projectId");
        if (folderId is { } f) form.Add(new StringContent(f.ToString()), "folderId");
        if (collectionId is { } c) form.Add(new StringContent(c.ToString()), "collectionId");

        foreach (var file in files)
        {
            var content = new StreamContent(file.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out var type) ? type : new("application/octet-stream");
            form.Add(content, "files", file.FileName);
        }

        using var response = await http.PostAsync("api/documents", form);
        return await ReadAsync<BatchUploadResult>(response);
    }

    /// <summary>The raw file response (headers read, body still streaming). The caller must dispose it.</summary>
    public async Task<HttpResponseMessage> ContentAsync(Guid id, bool download)
    {
        var response = await http.GetAsync($"api/documents/{id}/content" + Query(("download", download)), HttpCompletionOption.ResponseHeadersRead);
        if (response.IsSuccessStatusCode) return response;

        using (response) throw await ApiException.FromResponseAsync(response);
    }

    public Task RenameDocumentAsync(Guid id, string fileName) => SendAsync(HttpMethod.Put, $"api/documents/{id}/name", new { fileName });

    public Task MoveDocumentsAsync(IReadOnlyList<Guid> documentIds, Guid? folderId) =>
        SendAsync(HttpMethod.Post, "api/documents/move", new { documentIds, folderId });

    public Task DeleteDocumentAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/documents/{id}");

    public Task ReprocessDocumentAsync(Guid id) => SendAsync(HttpMethod.Post, $"api/documents/{id}/reprocess");

    // ----- Admin & audit -----------------------------------------------------------------------

    public Task<PagedResult<UserDto>> UsersAsync(int page, int pageSize, string? search) =>
        GetAsync<PagedResult<UserDto>>("api/admin/users" + Query(("page", page), ("pageSize", pageSize), ("search", search)));

    public Task CreateUserAsync(string email, string password, string displayName, UserRole role) =>
        SendAsync(HttpMethod.Post, "api/admin/users", new { email, password, displayName, role });

    public Task ChangeUserRoleAsync(Guid userId, UserRole role) => SendAsync(HttpMethod.Put, $"api/admin/users/{userId}/role", new { role });

    public Task SetUserActiveAsync(Guid userId, bool isActive) => SendAsync(HttpMethod.Put, $"api/admin/users/{userId}/active", new { isActive });

    public Task<PagedResult<AuditEntryDto>> AuditAsync(AuditQuery q) =>
        GetAsync<PagedResult<AuditEntryDto>>("api/audit-logs" + Query(
            ("projectId", q.ProjectId), ("action", q.Action), ("succeeded", q.Succeeded),
            ("from", q.From?.ToString("o")), ("to", q.To?.ToString("o")), ("page", q.Page), ("pageSize", q.PageSize)));

    // ----- Plumbing ----------------------------------------------------------------------------

    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await http.GetAsync(path);
        return await ReadAsync<T>(response);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: json) };
        using var response = await http.SendAsync(request);
        return await ReadAsync<T>(response);
    }

    private async Task SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: json) };
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw await ApiException.FromResponseAsync(response);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw await ApiException.FromResponseAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(json))!;
    }

    private static string Query(params (string Key, object? Value)[] values)
    {
        var parts = values
            .Where(v => v.Value is not null && v.Value is not string { Length: 0 })
            .Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value is bool b ? (b ? "true" : "false") : v.Value!.ToString()!)}");
        var query = string.Join("&", parts);
        return query.Length == 0 ? "" : "?" + query;
    }
}
