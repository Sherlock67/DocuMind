using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Features.Collections;
using DocuMind.Application.Features.Folders;
using DocuMind.Application.Features.Projects;
using DocuMind.Domain.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DocuMind.Api.Endpoints;

/// <summary>Projects, their folder trees and their collections.</summary>
internal static class OrganizationEndpoints
{
    public sealed record ProjectRequest(string Name, string? Description);

    public sealed record CreateFolderRequest(string Name, Guid? ParentId);

    public sealed record RenameRequest(string Name);

    public sealed record MoveFolderRequest(Guid? ParentId);

    public sealed record CollectionRequest(string Name, string? Description, CollectionVisibility Visibility = CollectionVisibility.Private);

    public sealed record AddMemberRequest(string Email, ProjectRole Role);

    public sealed record MemberRoleRequest(ProjectRole Role);

    public sealed record ShareRequest(string Email);

    public sealed record CollectionDocumentsRequest(IReadOnlyList<Guid> DocumentIds);

    public sealed record AddedResponse(int Added);

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/projects").WithTags("Projects");
        projects.MapPost("/", CreateProject).WithSummary("Create a project you will own (Managers and Admins).");
        projects.MapGet("/", ListProjects).WithSummary("Projects you are a member of, with your role and document counts by status.");
        projects.MapGet("/{id:guid}", GetProject).WithName("GetProject").WithSummary("Get a project with document counts by status.");
        projects.MapPut("/{id:guid}", UpdateProject).WithSummary("Rename a project or change its description.");
        projects.MapDelete("/{id:guid}", DeleteProject)
            .WithSummary("Delete a project. Must be empty unless ?force=true, which deletes all its documents too.");

        projects.MapGet("/{id:guid}/members", ListMembers).WithTags("Sharing").WithSummary("Who has access to the project, and with which role.");
        projects.MapPost("/{id:guid}/members", AddMember).WithTags("Sharing").WithSummary("Share the project with a user by email (Owner only).");
        projects.MapPut("/{id:guid}/members/{userId:guid}", ChangeMemberRole).WithTags("Sharing").WithSummary("Change a member's role (Owner only).");
        projects.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMember).WithTags("Sharing")
            .WithSummary("Remove a member (Owner), or leave the project (your own userId).");

        projects.MapGet("/{id:guid}/folders", GetFolderTree).WithTags("Folders").WithSummary("The project's folder tree with document counts.");
        projects.MapPost("/{id:guid}/folders", CreateFolder).WithTags("Folders").WithSummary("Create a folder (at the root, or under parentId).");
        projects.MapGet("/{id:guid}/collections", ListCollections).WithTags("Collections").WithSummary("The project's collections.");
        projects.MapPost("/{id:guid}/collections", CreateCollection).WithTags("Collections").WithSummary("Create a collection.");

        var folders = app.MapGroup("/api/folders").WithTags("Folders");
        folders.MapPut("/{id:guid}/name", RenameFolder).WithSummary("Rename a folder.");
        folders.MapPut("/{id:guid}/parent", MoveFolder).WithSummary("Move a folder under another folder, or to the root with parentId = null.");
        folders.MapDelete("/{id:guid}", DeleteFolder)
            .WithSummary("Delete a folder. Must be empty unless ?recursive=true, which deletes subfolders and their documents.");

        var collections = app.MapGroup("/api/collections").WithTags("Collections");
        collections.MapGet("/{id:guid}", GetCollection).WithName("GetCollection").WithSummary("Get a collection.");
        collections.MapPut("/{id:guid}", UpdateCollection).WithSummary("Rename a collection or change its description.");
        collections.MapDelete("/{id:guid}", DeleteCollection).WithSummary("Delete a collection (its documents are kept).");
        collections.MapPost("/{id:guid}/documents", AddToCollection).WithSummary("Add documents from the same project.");
        collections.MapDelete("/{id:guid}/documents/{documentId:guid}", RemoveFromCollection).WithSummary("Remove a document from the collection.");
        collections.MapGet("/{id:guid}/shares", ListShares).WithTags("Sharing").WithSummary("Users a collection is shared with.");
        collections.MapPost("/{id:guid}/shares", Share).WithTags("Sharing")
            .WithSummary("Share a collection (and read access to its documents) with a user by email.");
        collections.MapDelete("/{id:guid}/shares/{userId:guid}", Unshare).WithTags("Sharing").WithSummary("Stop sharing a collection with a user.");

        return app;
    }

    // ----- Projects -----------------------------------------------------------------------------

    private static async Task<CreatedAtRoute<ProjectDto>> CreateProject(ProjectRequest request, ISender sender, CancellationToken ct)
    {
        var project = await sender.Send(new CreateProjectCommand(request.Name, request.Description), ct);
        return TypedResults.CreatedAtRoute(project, "GetProject", new { id = project.Id });
    }

    private static async Task<Ok<PagedResult<ProjectDto>>> ListProjects(ISender sender, CancellationToken ct, int page = 1, int pageSize = 20, string? search = null) =>
        TypedResults.Ok(await sender.Send(new GetProjectsQuery(page, pageSize, search), ct));

    private static async Task<Ok<ProjectDto>> GetProject(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetProjectByIdQuery(id), ct));

    private static async Task<NoContent> UpdateProject(Guid id, ProjectRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new UpdateProjectCommand(id, request.Name, request.Description), ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteProject(Guid id, ISender sender, CancellationToken ct, bool force = false)
    {
        await sender.Send(new DeleteProjectCommand(id, force), ct);
        return TypedResults.NoContent();
    }

    // ----- Members ------------------------------------------------------------------------------

    private static async Task<Ok<IReadOnlyList<ProjectMemberDto>>> ListMembers(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetProjectMembersQuery(id), ct));

    private static async Task<Ok<ProjectMemberDto>> AddMember(Guid id, AddMemberRequest request, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new AddProjectMemberCommand(id, request.Email, request.Role), ct));

    private static async Task<NoContent> ChangeMemberRole(Guid id, Guid userId, MemberRoleRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new ChangeProjectMemberRoleCommand(id, userId, request.Role), ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveMember(Guid id, Guid userId, ISender sender, CancellationToken ct)
    {
        await sender.Send(new RemoveProjectMemberCommand(id, userId), ct);
        return TypedResults.NoContent();
    }

    // ----- Folders ------------------------------------------------------------------------------

    private static async Task<Ok<IReadOnlyList<FolderTreeNodeDto>>> GetFolderTree(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetFolderTreeQuery(id), ct));

    private static async Task<Created<FolderDto>> CreateFolder(Guid id, CreateFolderRequest request, ISender sender, CancellationToken ct)
    {
        var folder = await sender.Send(new CreateFolderCommand(id, request.Name, request.ParentId), ct);
        return TypedResults.Created($"/api/projects/{id}/folders", folder);
    }

    private static async Task<NoContent> RenameFolder(Guid id, RenameRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new RenameFolderCommand(id, request.Name), ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> MoveFolder(Guid id, MoveFolderRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new MoveFolderCommand(id, request.ParentId), ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteFolder(Guid id, ISender sender, CancellationToken ct, bool recursive = false)
    {
        await sender.Send(new DeleteFolderCommand(id, recursive), ct);
        return TypedResults.NoContent();
    }

    // ----- Collections --------------------------------------------------------------------------

    private static async Task<Ok<IReadOnlyList<CollectionDto>>> ListCollections(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetCollectionsQuery(id), ct));

    private static async Task<CreatedAtRoute<CollectionDto>> CreateCollection(Guid id, CollectionRequest request, ISender sender, CancellationToken ct)
    {
        var collection = await sender.Send(new CreateCollectionCommand(id, request.Name, request.Description, request.Visibility), ct);
        return TypedResults.CreatedAtRoute(collection, "GetCollection", new { id = collection.Id });
    }

    private static async Task<Ok<CollectionDto>> GetCollection(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetCollectionByIdQuery(id), ct));

    private static async Task<NoContent> UpdateCollection(Guid id, CollectionRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new UpdateCollectionCommand(id, request.Name, request.Description, request.Visibility), ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteCollection(Guid id, ISender sender, CancellationToken ct)
    {
        await sender.Send(new DeleteCollectionCommand(id), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<AddedResponse>> AddToCollection(Guid id, CollectionDocumentsRequest request, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(new AddedResponse(await sender.Send(new AddDocumentsToCollectionCommand(id, request.DocumentIds ?? []), ct)));

    private static async Task<NoContent> RemoveFromCollection(Guid id, Guid documentId, ISender sender, CancellationToken ct)
    {
        await sender.Send(new RemoveDocumentFromCollectionCommand(id, documentId), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<CollectionShareDto>>> ListShares(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetCollectionSharesQuery(id), ct));

    private static async Task<Ok<CollectionShareDto>> Share(Guid id, ShareRequest request, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new ShareCollectionCommand(id, request.Email), ct));

    private static async Task<NoContent> Unshare(Guid id, Guid userId, ISender sender, CancellationToken ct)
    {
        await sender.Send(new UnshareCollectionCommand(id, userId), ct);
        return TypedResults.NoContent();
    }
}
