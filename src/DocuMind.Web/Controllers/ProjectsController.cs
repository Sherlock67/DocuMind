using DocuMind.Web.Api;
using DocuMind.Web.Infrastructure;
using DocuMind.Web.Models;
using DocuMind.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace DocuMind.Web.Controllers;

public sealed class ProjectsController(DocuMindApiClient api) : AppController
{
    [HttpGet("/"), HttpGet("/projects")]
    public async Task<IActionResult> Index(string? q)
    {
        ViewData["Search"] = q;
        ViewData["CanCreate"] = IsManagerOrAbove;
        return View((await api.ProjectsAsync(search: q)).Items);
    }

    [HttpPost("/projects")]
    public async Task<IActionResult> Create(string name, string? description)
    {
        var project = await api.CreateProjectAsync(name, description);
        Success($"Project “{project.Name}” created. Upload documents or share it with your team.");
        return Redirect($"/projects/{project.Id}");
    }

    /// <summary>The project workspace: folder tree, collections and the filtered document list.</summary>
    [HttpGet("/projects/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, [FromQuery] WorkspaceFilter filter)
    {
        var project = await api.ProjectAsync(id);
        var foldersTask = api.FolderTreeAsync(id);
        var collectionsTask = api.CollectionsAsync(id);
        var documentsTask = api.DocumentsAsync(new DocumentQuery(
            id,
            FolderId: filter.Folder,
            IncludeSubfolders: filter.Folder is not null && filter.Sub,
            RootOnly: filter.Root && filter.Folder is null,
            CollectionId: filter.Collection,
            Status: filter.Status,
            Search: filter.Q,
            SortBy: filter.Sort,
            Descending: filter.Desc,
            Page: Math.Max(1, filter.Page)));

        await Task.WhenAll(foldersTask, collectionsTask, documentsTask);
        return View(new WorkspaceViewModel(project, AccessFor(project), MyId, foldersTask.Result, collectionsTask.Result, documentsTask.Result, filter));
    }

    [HttpPost("/projects/{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, string name, string? description)
    {
        await api.UpdateProjectAsync(id, name, description);
        Success("Project updated.");
        return Redirect($"/projects/{id}");
    }

    [HttpPost("/projects/{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, bool force)
    {
        await api.DeleteProjectAsync(id, force);
        Success("Project deleted.");
        return Redirect("/");
    }

    // ----- Members (sharing) ---------------------------------------------------------------------

    [HttpGet("/projects/{id:guid}/members")]
    public async Task<IActionResult> Members(Guid id)
    {
        var project = await api.ProjectAsync(id);
        return View(new MembersViewModel(project, AccessFor(project), await api.MembersAsync(id), MyId));
    }

    [HttpPost("/projects/{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, string email, ProjectRole role)
    {
        await api.AddMemberAsync(id, email.Trim(), role);
        Success($"{email.Trim()} now has {role} access.");
        return Redirect($"/projects/{id}/members");
    }

    [HttpPost("/projects/{id:guid}/members/{userId:guid}/role")]
    public async Task<IActionResult> ChangeMemberRole(Guid id, Guid userId, ProjectRole role)
    {
        await api.ChangeMemberRoleAsync(id, userId, role);
        Success($"Role changed to {role}.");
        return Redirect($"/projects/{id}/members");
    }

    [HttpPost("/projects/{id:guid}/members/{userId:guid}/remove")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        await api.RemoveMemberAsync(id, userId);
        if (userId == MyId)
        {
            Success("You left the project.");
            return Redirect("/");
        }

        Success("Member removed.");
        return Redirect($"/projects/{id}/members");
    }
}

public sealed class FoldersController(DocuMindApiClient api) : AppController
{
    [HttpPost("/projects/{projectId:guid}/folders")]
    public async Task<IActionResult> Create(Guid projectId, string name, Guid? parentId, string? returnUrl)
    {
        await api.CreateFolderAsync(projectId, name.Trim(), parentId);
        Success($"Folder “{name.Trim()}” created.");
        return Back(returnUrl, $"/projects/{projectId}");
    }

    [HttpPost("/folders/{id:guid}/rename")]
    public async Task<IActionResult> Rename(Guid id, string name, string? returnUrl)
    {
        await api.RenameFolderAsync(id, name.Trim());
        Success("Folder renamed.");
        return Back(returnUrl);
    }

    [HttpPost("/folders/{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, Guid? parentId, string? returnUrl)
    {
        await api.MoveFolderAsync(id, parentId);
        Success("Folder moved.");
        return Back(returnUrl);
    }

    [HttpPost("/folders/{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, Guid projectId, bool recursive)
    {
        await api.DeleteFolderAsync(id, recursive);
        Success(recursive ? "Folder and everything in it deleted." : "Folder deleted.");
        return Redirect($"/projects/{projectId}");
    }
}

public sealed class CollectionsController(DocuMindApiClient api) : AppController
{
    [HttpPost("/projects/{projectId:guid}/collections")]
    public async Task<IActionResult> Create(Guid projectId, string name, string? description, CollectionVisibility visibility, string? returnUrl)
    {
        await api.CreateCollectionAsync(projectId, name.Trim(), description, visibility);
        Success($"Collection “{name.Trim()}” created.");
        return Back(returnUrl, $"/projects/{projectId}");
    }

    [HttpPost("/collections/{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, string name, string? description, CollectionVisibility visibility, string? returnUrl)
    {
        await api.UpdateCollectionAsync(id, name.Trim(), description, visibility);
        Success("Collection updated.");
        return Back(returnUrl);
    }

    [HttpPost("/collections/{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, Guid projectId)
    {
        await api.DeleteCollectionAsync(id);
        Success("Collection deleted. Its documents were not touched.");
        return Redirect($"/projects/{projectId}");
    }

    [HttpPost("/collections/{id:guid}/documents")]
    public async Task<IActionResult> AddDocument(Guid id, Guid documentId, string? returnUrl)
    {
        await api.AddToCollectionAsync(id, [documentId]);
        Success("Added to the collection.");
        return Back(returnUrl);
    }

    [HttpPost("/collections/{id:guid}/documents/{documentId:guid}/remove")]
    public async Task<IActionResult> RemoveDocument(Guid id, Guid documentId, string? returnUrl)
    {
        await api.RemoveFromCollectionAsync(id, documentId);
        Success("Removed from the collection.");
        return Back(returnUrl);
    }

    // ----- Sharing -------------------------------------------------------------------------------

    [HttpGet("/collections/{id:guid}/shares")]
    public async Task<IActionResult> Shares(Guid id)
    {
        var collection = await api.CollectionAsync(id);
        var project = await api.ProjectAsync(collection.ProjectId);
        return View(new SharesViewModel(collection, project, AccessFor(project), await api.SharesAsync(id)));
    }

    [HttpPost("/collections/{id:guid}/shares")]
    public async Task<IActionResult> Share(Guid id, string email)
    {
        await api.ShareCollectionAsync(id, email.Trim());
        Success($"Shared with {email.Trim()}.");
        return Redirect($"/collections/{id}/shares");
    }

    [HttpPost("/collections/{id:guid}/shares/{userId:guid}/remove")]
    public async Task<IActionResult> Unshare(Guid id, Guid userId)
    {
        await api.UnshareCollectionAsync(id, userId);
        Success("Stopped sharing.");
        return Redirect($"/collections/{id}/shares");
    }
}
