using DocuMind.Domain.Common;

namespace DocuMind.Domain.Projects;

/// <summary>A node in a project's folder tree. Root folders have no parent.</summary>
public sealed class Folder : Entity, IAggregateRoot
{
    public const int MaxNameLength = 120;
    public const int MaxDepth = 10;

    private Folder() { } // EF Core

    public Guid ProjectId { get; private set; }
    public Guid? ParentId { get; private set; }
    public string Name { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Folder Create(Guid projectId, Folder? parent, string name, DateTimeOffset now)
    {
        if (parent is not null && parent.ProjectId != projectId)
            throw new DomainException("A parent folder must belong to the same project.");

        return new Folder
        {
            ProjectId = projectId,
            ParentId = parent?.Id,
            Name = ValidateName(name),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = ValidateName(name);
        UpdatedAt = now;
    }

    /// <summary>
    /// Re-parents this folder. <paramref name="projectFolders"/> must contain every folder of the
    /// project so the move can be checked for cycles and depth.
    /// </summary>
    public void MoveTo(Folder? newParent, IReadOnlyCollection<Folder> projectFolders, DateTimeOffset now)
    {
        if (newParent is not null)
        {
            if (newParent.ProjectId != ProjectId)
                throw new DomainException("Folders cannot be moved between projects.");
            if (newParent.Id == Id || FolderTree.IsDescendantOf(newParent.Id, Id, projectFolders))
                throw new DomainException("A folder cannot be moved into itself or one of its subfolders.");
            if (FolderTree.Depth(newParent.Id, projectFolders) + FolderTree.Height(Id, projectFolders) > MaxDepth)
                throw new DomainException($"Folders can be nested at most {MaxDepth} levels deep.");
        }

        ParentId = newParent?.Id;
        UpdatedAt = now;
    }

    private static string ValidateName(string name)
    {
        var value = Names.Require(name, MaxNameLength, "Folder name");
        if (value.IndexOfAny(['/', '\\']) >= 0) throw new DomainException("Folder name cannot contain '/' or '\\'.");
        return value;
    }
}

/// <summary>Pure helpers over a project's flat folder list.</summary>
public static class FolderTree
{
    /// <summary>True when <paramref name="folderId"/> sits somewhere below <paramref name="ancestorId"/>.</summary>
    public static bool IsDescendantOf(Guid folderId, Guid ancestorId, IReadOnlyCollection<Folder> folders)
    {
        var byId = folders.ToDictionary(f => f.Id);
        var current = byId.GetValueOrDefault(folderId)?.ParentId;
        var guard = 0;
        while (current is { } id && guard++ < folders.Count)
        {
            if (id == ancestorId) return true;
            current = byId.GetValueOrDefault(id)?.ParentId;
        }
        return false;
    }

    /// <summary>The folder and every folder below it.</summary>
    public static IReadOnlyList<Guid> SelfAndDescendants(Guid folderId, IReadOnlyCollection<Folder> folders)
    {
        var children = folders.Where(f => f.ParentId is not null).ToLookup(f => f.ParentId!.Value, f => f.Id);
        var result = new List<Guid> { folderId };
        for (var i = 0; i < result.Count; i++)
            result.AddRange(children[result[i]]);
        return result;
    }

    /// <summary>1 for a root folder, 2 for its children, ...</summary>
    public static int Depth(Guid folderId, IReadOnlyCollection<Folder> folders)
    {
        var byId = folders.ToDictionary(f => f.Id);
        var depth = 0;
        for (Guid? id = folderId; id is { } current && byId.TryGetValue(current, out var f) && depth <= folders.Count; id = f.ParentId)
            depth++;
        return depth;
    }

    /// <summary>Levels in the subtree rooted at the folder, itself included.</summary>
    public static int Height(Guid folderId, IReadOnlyCollection<Folder> folders)
    {
        var children = folders.Where(f => f.ParentId is not null).ToLookup(f => f.ParentId!.Value, f => f.Id);
        int Measure(Guid id, int guard) => guard > folders.Count ? 0 : 1 + children[id].Select(c => Measure(c, guard + 1)).DefaultIfEmpty(0).Max();
        return Measure(folderId, 0);
    }
}
