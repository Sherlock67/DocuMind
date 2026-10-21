using DocuMind.Application.Abstractions.Data;
using DocuMind.Domain.Chat;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Infrastructure.Persistence;

internal sealed class DocumentRepository(AppDbContext db) : IDocumentRepository
{
    public Task<Document?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Document>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct);

    public Task<Document?> GetForIngestionAsync(Guid id, CancellationToken ct) =>
        db.Documents.Include(d => d.Chunks).Include(d => d.Pages).AsSplitQuery().FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<Document?> FindByContentHashAsync(Guid projectId, string contentHash, CancellationToken ct) =>
        db.Documents.FirstOrDefaultAsync(d => d.ProjectId == projectId && d.ContentHash == contentHash, ct);

    public async Task<IReadOnlyList<Guid>> GetUnfinishedIdsAsync(CancellationToken ct) =>
        await db.Documents
            .Where(d => d.Status == DocumentStatus.Queued || d.Status == DocumentStatus.Processing)
            .OrderBy(d => d.UploadedAt)
            .Select(d => d.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Document>> ListByProjectAsync(Guid projectId, CancellationToken ct) =>
        await db.Documents.Where(d => d.ProjectId == projectId).ToListAsync(ct);

    public async Task<IReadOnlyList<Document>> ListByFoldersAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken ct) =>
        await db.Documents.Where(d => d.FolderId != null && folderIds.Contains(d.FolderId.Value)).ToListAsync(ct);

    public Task<bool> AnyInProjectAsync(Guid projectId, CancellationToken ct) =>
        db.Documents.AnyAsync(d => d.ProjectId == projectId, ct);

    public Task<bool> AnyInFoldersAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken ct) =>
        db.Documents.AnyAsync(d => d.FolderId != null && folderIds.Contains(d.FolderId.Value), ct);

    public async Task<IReadOnlyList<Guid>> FilterIdsInProjectAsync(Guid projectId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Documents.Where(d => d.ProjectId == projectId && ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct);

    public void Add(Document document) => db.Documents.Add(document);

    public void Remove(Document document) => db.Documents.Remove(document);
}

internal sealed class ProjectRepository(AppDbContext db) : IProjectRepository
{
    public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct) => db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Project?> GetWithMembersAsync(Guid id, CancellationToken ct) =>
        db.Projects.Include(p => p.Members).FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => db.Projects.AnyAsync(p => p.Id == id, ct);

    public void Add(Project project) => db.Projects.Add(project);

    public void Remove(Project project) => db.Projects.Remove(project);
}

internal sealed class FolderRepository(AppDbContext db) : IFolderRepository
{
    public Task<Folder?> GetByIdAsync(Guid id, CancellationToken ct) => db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<IReadOnlyList<Folder>> ListByProjectAsync(Guid projectId, CancellationToken ct) =>
        await db.Folders.Where(f => f.ProjectId == projectId).ToListAsync(ct);

    public void Add(Folder folder) => db.Folders.Add(folder);

    public void Remove(Folder folder) => db.Folders.Remove(folder);
}

internal sealed class CollectionRepository(AppDbContext db) : ICollectionRepository
{
    public Task<Collection?> GetByIdAsync(Guid id, CancellationToken ct) => db.Collections.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Collection?> GetWithItemsAsync(Guid id, CancellationToken ct) =>
        db.Collections.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Collection?> GetWithSharesAsync(Guid id, CancellationToken ct) =>
        db.Collections.Include(c => c.Shares).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<bool> NameExistsAsync(Guid projectId, string name, Guid? excludeId, CancellationToken ct) =>
        db.Collections.AnyAsync(c => c.ProjectId == projectId && c.Name.ToLower() == name.ToLower() && c.Id != excludeId, ct);

    public void Add(Collection collection) => db.Collections.Add(collection);

    public void Remove(Collection collection) => db.Collections.Remove(collection);
}

internal sealed class ChatSessionRepository(AppDbContext db) : IChatSessionRepository
{
    public Task<ChatSession?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(Guid sessionId, int count, CancellationToken ct)
    {
        var latestFirst = await db.Set<ChatMessage>()
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Role)
            .Take(count)
            .ToListAsync(ct);

        latestFirst.Reverse();

        // Never start the replayed history on an assistant turn.
        return latestFirst.SkipWhile(m => m.Role == ChatRole.Assistant).ToList();
    }

    public void Add(ChatSession session) => db.ChatSessions.Add(session);
}
