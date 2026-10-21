using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Common;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Folders;

public sealed record FolderDto(Guid Id, Guid ProjectId, Guid? ParentId, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record FolderTreeNodeDto(
    Guid Id,
    Guid? ParentId,
    string Name,
    int DocumentCount,
    int TotalDocumentCount,
    IReadOnlyList<FolderTreeNodeDto> Children);

// ----- Commands ---------------------------------------------------------------------------------

public sealed record CreateFolderCommand(Guid ProjectId, string Name, Guid? ParentId = null) : ICommand<FolderDto>;

public sealed record RenameFolderCommand(Guid FolderId, string Name) : ICommand<Unit>;

/// <summary>Moves a folder (with everything below it) under another folder, or to the project root when null.</summary>
public sealed record MoveFolderCommand(Guid FolderId, Guid? NewParentId) : ICommand<Unit>;

/// <summary>
/// Deletes a folder. Without <see cref="Recursive"/> it must be empty; with it, every subfolder
/// and every document inside them (rows, chunks and stored files) is deleted too.
/// </summary>
public sealed record DeleteFolderCommand(Guid FolderId, bool Recursive = false) : ICommand<Unit>;

internal sealed class CreateFolderCommandValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderCommandValidator()
    {
        RuleFor(x => x.ProjectId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Folder.MaxNameLength);
    }
}

internal sealed class RenameFolderCommandValidator : AbstractValidator<RenameFolderCommand>
{
    public RenameFolderCommandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Folder.MaxNameLength);
}

internal static class SiblingNames
{
    public static void EnsureUnique(IEnumerable<Folder> projectFolders, Guid? parentId, string name, Guid? excludeId)
    {
        var trimmed = name.Trim();
        if (projectFolders.Any(f => f.ParentId == parentId && f.Id != excludeId && string.Equals(f.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"A folder named '{trimmed}' already exists here.");
    }
}

internal sealed class CreateFolderCommandHandler(
    IAccessControl access,
    IFolderRepository folders,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<CreateFolderCommand, FolderDto>
{
    public async Task<FolderDto> Handle(CreateFolderCommand command, CancellationToken ct)
    {
        await access.RequireProjectAsync(command.ProjectId, ProjectRole.Editor, ct);

        var projectFolders = await folders.ListByProjectAsync(command.ProjectId, ct);

        Folder? parent = null;
        if (command.ParentId is { } parentId)
        {
            parent = projectFolders.FirstOrDefault(f => f.Id == parentId)
                ?? throw new NotFoundException(nameof(Folder), parentId);
            if (FolderTree.Depth(parentId, projectFolders) >= Folder.MaxDepth)
                throw new DomainException($"Folders can be nested at most {Folder.MaxDepth} levels deep.");
        }

        SiblingNames.EnsureUnique(projectFolders, command.ParentId, command.Name, excludeId: null);

        var folder = Folder.Create(command.ProjectId, parent, command.Name, clock.GetUtcNow());
        folders.Add(folder);
        await unitOfWork.SaveChangesAsync(ct);

        return new FolderDto(folder.Id, folder.ProjectId, folder.ParentId, folder.Name, folder.CreatedAt, folder.UpdatedAt);
    }
}

internal sealed class RenameFolderCommandHandler(IFolderRepository folders, IUnitOfWork unitOfWork, IAccessControl access, TimeProvider clock)
    : ICommandHandler<RenameFolderCommand, Unit>
{
    public async Task<Unit> Handle(RenameFolderCommand command, CancellationToken ct)
    {
        await access.RequireFolderAsync(command.FolderId, ProjectRole.Editor, ct);

        var folder = await folders.GetByIdAsync(command.FolderId, ct)
            ?? throw new NotFoundException(nameof(Folder), command.FolderId);

        SiblingNames.EnsureUnique(await folders.ListByProjectAsync(folder.ProjectId, ct), folder.ParentId, command.Name, folder.Id);

        folder.Rename(command.Name, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

internal sealed class MoveFolderCommandHandler(IFolderRepository folders, IUnitOfWork unitOfWork, IAccessControl access, TimeProvider clock)
    : ICommandHandler<MoveFolderCommand, Unit>
{
    public async Task<Unit> Handle(MoveFolderCommand command, CancellationToken ct)
    {
        await access.RequireFolderAsync(command.FolderId, ProjectRole.Editor, ct);

        var folder = await folders.GetByIdAsync(command.FolderId, ct)
            ?? throw new NotFoundException(nameof(Folder), command.FolderId);

        var projectFolders = await folders.ListByProjectAsync(folder.ProjectId, ct);

        Folder? newParent = null;
        if (command.NewParentId is { } parentId)
        {
            newParent = projectFolders.FirstOrDefault(f => f.Id == parentId)
                ?? throw new NotFoundException(nameof(Folder), parentId);
        }

        SiblingNames.EnsureUnique(projectFolders, command.NewParentId, folder.Name, folder.Id);

        folder.MoveTo(newParent, projectFolders, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

internal sealed class DeleteFolderCommandHandler(
    IFolderRepository folders,
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    DocumentFileCleanup cleanup) : ICommandHandler<DeleteFolderCommand, Unit>
{
    public async Task<Unit> Handle(DeleteFolderCommand command, CancellationToken ct)
    {
        await access.RequireFolderAsync(command.FolderId, ProjectRole.Editor, ct);

        var folder = await folders.GetByIdAsync(command.FolderId, ct)
            ?? throw new NotFoundException(nameof(Folder), command.FolderId);

        var projectFolders = await folders.ListByProjectAsync(folder.ProjectId, ct);
        var subtree = FolderTree.SelfAndDescendants(folder.Id, projectFolders);

        IReadOnlyList<Document> doomed = [];
        if (command.Recursive)
        {
            doomed = await documents.ListByFoldersAsync(subtree, ct);
            foreach (var document in doomed)
                documents.Remove(document);
        }
        else if (subtree.Count > 1 || await documents.AnyInFoldersAsync(subtree, ct))
        {
            throw new DomainException("The folder is not empty. Move or delete its contents first, or pass recursive=true.");
        }

        foreach (var id in subtree)
            folders.Remove(projectFolders.First(f => f.Id == id));

        await unitOfWork.SaveChangesAsync(ct);

        foreach (var document in doomed)
            audit.Record(AuditActions.DocumentDeleted, "document", document.Id, folder.ProjectId,
                new Dictionary<string, string> { ["fileName"] = document.FileName, ["via"] = "folder.deleted" });
        audit.Record(AuditActions.FolderDeleted, "folder", folder.Id, folder.ProjectId, new Dictionary<string, string>
        {
            ["name"] = folder.Name,
            ["subfolders"] = (subtree.Count - 1).ToString(),
            ["documentsDeleted"] = doomed.Count.ToString(),
        });

        await cleanup.DeleteFilesAsync(doomed.Select(d => d.StoragePath), ct);
        return Unit.Value;
    }
}

// ----- Queries ----------------------------------------------------------------------------------

/// <summary>The project's full folder tree with direct and recursive document counts.</summary>
public sealed record GetFolderTreeQuery(Guid ProjectId) : IQuery<IReadOnlyList<FolderTreeNodeDto>>;

internal sealed class GetFolderTreeQueryHandler(IReadDbContext db, IAccessControl access)
    : IQueryHandler<GetFolderTreeQuery, IReadOnlyList<FolderTreeNodeDto>>
{
    public async Task<IReadOnlyList<FolderTreeNodeDto>> Handle(GetFolderTreeQuery query, CancellationToken ct)
    {
        await access.RequireProjectAsync(query.ProjectId, ProjectRole.Viewer, ct);

        var folders = await db.Folders
            .Where(f => f.ProjectId == query.ProjectId)
            .Select(f => new { f.Id, f.ParentId, f.Name })
            .ToListAsync(ct);

        var counts = await db.Documents
            .Where(d => d.ProjectId == query.ProjectId && d.FolderId != null)
            .GroupBy(d => d.FolderId!.Value)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FolderId, x => x.Count, ct);

        var children = folders.ToLookup(f => f.ParentId);

        FolderTreeNodeDto Build(Guid id, Guid? parentId, string name)
        {
            var kids = children[id].OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => Build(c.Id, c.ParentId, c.Name)).ToList();
            var own = counts.GetValueOrDefault(id);
            return new FolderTreeNodeDto(id, parentId, name, own, own + kids.Sum(k => k.TotalDocumentCount), kids);
        }

        return children[null].OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => Build(f.Id, null, f.Name)).ToList();
    }
}
