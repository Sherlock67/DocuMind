using DocuMind.Domain.Auditing;
using DocuMind.Domain.Chat;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;

namespace DocuMind.Application.Abstractions.Data;

// ---------------------------------------------------------------------------------------------
// Write side: change-tracked aggregate access used only by command handlers.
// ---------------------------------------------------------------------------------------------

public interface IDocumentRepository
{
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Document>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Loads the document with its chunks and pages so they can be replaced atomically.</summary>
    Task<Document?> GetForIngestionAsync(Guid id, CancellationToken ct);

    Task<Document?> FindByContentHashAsync(Guid projectId, string contentHash, CancellationToken ct);

    /// <summary>Documents left Queued/Processing (e.g. by a crash) that must be re-queued on startup.</summary>
    Task<IReadOnlyList<Guid>> GetUnfinishedIdsAsync(CancellationToken ct);

    Task<IReadOnlyList<Document>> ListByProjectAsync(Guid projectId, CancellationToken ct);

    Task<IReadOnlyList<Document>> ListByFoldersAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken ct);

    Task<bool> AnyInProjectAsync(Guid projectId, CancellationToken ct);

    Task<bool> AnyInFoldersAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken ct);

    /// <summary>The subset of <paramref name="ids"/> that exist and belong to <paramref name="projectId"/>.</summary>
    Task<IReadOnlyList<Guid>> FilterIdsInProjectAsync(Guid projectId, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    void Add(Document document);
    void Remove(Document document);
}

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Loads the project with its members, for membership changes.</summary>
    Task<Project?> GetWithMembersAsync(Guid id, CancellationToken ct);

    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
    void Add(Project project);
    void Remove(Project project);
}

public interface IFolderRepository
{
    Task<Folder?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Every folder of the project (folder trees are small), for cycle checks and subtree operations.</summary>
    Task<IReadOnlyList<Folder>> ListByProjectAsync(Guid projectId, CancellationToken ct);

    void Add(Folder folder);
    void Remove(Folder folder);
}

public interface ICollectionRepository
{
    Task<Collection?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Collection?> GetWithItemsAsync(Guid id, CancellationToken ct);
    Task<Collection?> GetWithSharesAsync(Guid id, CancellationToken ct);
    Task<bool> NameExistsAsync(Guid projectId, string name, Guid? excludeId, CancellationToken ct);
    void Add(Collection collection);
    void Remove(Collection collection);
}

public interface IChatSessionRepository
{
    Task<ChatSession?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Last <paramref name="count"/> messages, oldest first, for replaying conversation context.</summary>
    Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int count, CancellationToken ct);

    void Add(ChatSession session);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------
// Read side: no-tracking queryables used only by query handlers. Backed by a separate
// DbContext so it can point at a read replica without touching the write model.
// ---------------------------------------------------------------------------------------------

public interface IReadDbContext
{
    IQueryable<Project> Projects { get; }
    IQueryable<ProjectMember> ProjectMembers { get; }
    IQueryable<Folder> Folders { get; }
    IQueryable<Collection> Collections { get; }
    IQueryable<CollectionItem> CollectionItems { get; }
    IQueryable<CollectionShare> CollectionShares { get; }
    IQueryable<AuditEntry> AuditEntries { get; }
    IQueryable<Document> Documents { get; }
    IQueryable<DocumentPage> DocumentPages { get; }
    IQueryable<DocumentChunk> DocumentChunks { get; }
    IQueryable<ChatSession> ChatSessions { get; }
    IQueryable<ChatMessage> ChatMessages { get; }
}
