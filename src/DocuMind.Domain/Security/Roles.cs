namespace DocuMind.Domain.Security;

/// <summary>
/// System-wide role. Ordered, so a higher value includes everything below it.
/// </summary>
public enum UserRole
{
    /// <summary>Works in projects shared with them; can create private collections.</summary>
    User = 0,

    /// <summary>Can also create projects (becoming their Owner) and share them.</summary>
    Manager = 1,

    /// <summary>Full access to every project, user administration and the global audit log.</summary>
    Admin = 2,
}

/// <summary>
/// A user's role within one project. Ordered, so a higher value includes everything below it.
/// </summary>
public enum ProjectRole
{
    /// <summary>Read, preview, download, search and chat over the project's documents.</summary>
    Viewer = 0,

    /// <summary>Also upload, rename, move, delete and reprocess documents; manage folders and project collections.</summary>
    Editor = 1,

    /// <summary>Also rename/delete the project, manage members and read the project's audit log.</summary>
    Owner = 2,
}

public enum CollectionVisibility
{
    /// <summary>Visible to its owner and the users it is explicitly shared with.</summary>
    Private = 0,

    /// <summary>Visible to every member of the project.</summary>
    Project = 1,
}
