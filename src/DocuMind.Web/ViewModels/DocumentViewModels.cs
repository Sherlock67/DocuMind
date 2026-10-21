using System.ComponentModel.DataAnnotations;
using DocuMind.Web.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace DocuMind.Web.ViewModels;

/// <summary>Query-string state of the project workspace (which folder/collection, filters, sort, page).</summary>
public sealed class WorkspaceFilter
{
    public Guid? Folder { get; set; }
    public bool Root { get; set; }
    public Guid? Collection { get; set; }
    public bool Sub { get; set; }
    public DocumentStatus? Status { get; set; }
    public string? Q { get; set; }
    public DocumentSort Sort { get; set; } = DocumentSort.UploadedAt;
    public bool Desc { get; set; } = true;

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    public WorkspaceFilter With(Action<WorkspaceFilter> change)
    {
        var copy = (WorkspaceFilter)MemberwiseClone();
        change(copy);
        return copy;
    }

    public string ToUrl(Guid projectId)
    {
        var q = new Dictionary<string, string?>();
        if (Folder is { } f) q["folder"] = f.ToString();
        if (Root) q["root"] = "true";
        if (Collection is { } c) q["collection"] = c.ToString();
        if (Sub) q["sub"] = "true";
        if (Status is { } s) q["status"] = s.ToString();
        if (!string.IsNullOrWhiteSpace(Q)) q["q"] = Q;
        if (Sort != DocumentSort.UploadedAt) q["sort"] = Sort.ToString();
        if (!Desc) q["desc"] = "false";
        if (Page > 1) q["page"] = Page.ToString();
        return QueryHelpers.AddQueryString($"/projects/{projectId}", q);
    }
}

public sealed record FolderOption(Guid Id, string Path, int Depth, IReadOnlySet<Guid> Subtree);

public sealed record WorkspaceViewModel(
    ProjectDto Project,
    ProjectAccess Access,
    Guid MyId,
    IReadOnlyList<FolderTreeNode> Folders,
    IReadOnlyList<CollectionDto> Collections,
    PagedResult<DocumentDto> Documents,
    WorkspaceFilter Filter)
{
    public IReadOnlyList<FolderOption> FolderOptions { get; } = FolderTree.Flatten(Folders);

    public FolderOption? SelectedFolder => Filter.Folder is { } id ? FolderOptions.FirstOrDefault(f => f.Id == id) : null;

    public CollectionDto? SelectedCollection => Filter.Collection is { } id ? Collections.FirstOrDefault(c => c.Id == id) : null;

    public IReadOnlyList<CollectionDto> ManageableCollections => [.. Collections.Where(c => Access.CanManage(c, MyId))];

    public string CurrentUrl => Filter.ToUrl(Project.Id);

    public string ScopeTitle =>
        SelectedCollection is { } c ? $"Collection: {c.Name}"
        : SelectedFolder is { } f ? f.Path
        : Filter.Root ? "Project root"
        : "All documents";
}

public static class FolderTree
{
    public static IReadOnlyList<FolderOption> Flatten(IReadOnlyList<FolderTreeNode> roots)
    {
        var result = new List<FolderOption>();
        void Walk(IEnumerable<FolderTreeNode> nodes, string prefix, int depth)
        {
            foreach (var n in nodes)
            {
                var path = prefix.Length == 0 ? n.Name : $"{prefix} / {n.Name}";
                result.Add(new FolderOption(n.Id, path, depth, Subtree(n)));
                Walk(n.Children, path, depth + 1);
            }
        }
        Walk(roots, "", 0);
        return result;
    }

    private static HashSet<Guid> Subtree(FolderTreeNode node)
    {
        var ids = new HashSet<Guid> { node.Id };
        foreach (var child in node.Children) ids.UnionWith(Subtree(child));
        return ids;
    }
}

public sealed record UploadResultViewModel(ProjectDto Project, string Destination, BatchUploadResult Result);

public sealed record DocumentDetailsViewModel(
    DocumentDto Document,
    ProjectDto Project,
    ProjectAccess Access,
    DocumentPreviewDto? Preview,
    IReadOnlyList<FolderOption> FolderOptions,
    IReadOnlyList<CollectionDto> ManageableCollections)
{
    public string? FolderPath => Document.FolderId is { } id ? FolderOptions.FirstOrDefault(f => f.Id == id)?.Path : null;
}

public sealed record MembersViewModel(ProjectDto Project, ProjectAccess Access, IReadOnlyList<ProjectMemberDto> Members, Guid MyId);

public sealed record SharesViewModel(CollectionDto Collection, ProjectDto Project, ProjectAccess Access, IReadOnlyList<CollectionShareDto> Shares);

public sealed record UsersViewModel(PagedResult<UserDto> Users, string? Search, Guid MyId);

public sealed class AuditFilter
{
    public Guid? ProjectId { get; set; }
    public string? Action { get; set; }
    public bool? Succeeded { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    public string PageUrl(int page)
    {
        var q = new Dictionary<string, string?>
        {
            ["projectId"] = ProjectId?.ToString(),
            ["action"] = Action,
            ["succeeded"] = Succeeded?.ToString().ToLowerInvariant(),
            ["from"] = From?.ToString("yyyy-MM-dd"),
            ["to"] = To?.ToString("yyyy-MM-dd"),
            ["page"] = page.ToString(),
        };
        return QueryHelpers.AddQueryString("/audit", q.Where(kv => !string.IsNullOrEmpty(kv.Value))!);
    }
}

public sealed record AuditViewModel(PagedResult<AuditEntryDto> Entries, AuditFilter Filter, ProjectDto? Project);
