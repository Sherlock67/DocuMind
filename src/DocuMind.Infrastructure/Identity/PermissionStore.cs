using System.Data;
using System.Text.Json;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Domain.Auditing;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using DocuMind.Infrastructure.Persistence;
using DocuMind.Infrastructure.Search;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Infrastructure.Identity;

/// <summary>Authorization lookups on the write (primary) database, never the read replica.</summary>
internal sealed class PermissionStore(AppDbContext db) : IPermissionStore
{
    public Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId, ct);

    public Task<ProjectRole?> GetProjectRoleAsync(Guid userId, Guid projectId, CancellationToken ct) =>
        db.Set<ProjectMember>().AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(ct);

    public Task<DocumentAccessInfo?> GetDocumentAccessAsync(Guid userId, Guid documentId, CancellationToken ct) =>
        db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new DocumentAccessInfo(
                d.Id,
                d.ProjectId,
                db.Set<ProjectMember>().Where(m => m.ProjectId == d.ProjectId && m.UserId == userId).Select(m => (ProjectRole?)m.Role).FirstOrDefault(),
                db.Set<CollectionItem>().Any(i => i.DocumentId == d.Id &&
                    db.Set<CollectionShare>().Any(s => s.CollectionId == i.CollectionId && s.UserId == userId))))
            .FirstOrDefaultAsync(ct);

    public Task<CollectionAccessInfo?> GetCollectionAccessAsync(Guid userId, Guid collectionId, CancellationToken ct) =>
        db.Collections.AsNoTracking()
            .Where(c => c.Id == collectionId)
            .Select(c => new CollectionAccessInfo(
                c.Id,
                c.ProjectId,
                c.OwnerId,
                c.Visibility,
                db.Set<ProjectMember>().Where(m => m.ProjectId == c.ProjectId && m.UserId == userId).Select(m => (ProjectRole?)m.Role).FirstOrDefault(),
                db.Set<CollectionShare>().Any(s => s.CollectionId == c.Id && s.UserId == userId)))
            .FirstOrDefaultAsync(ct);

    public Task<Guid?> GetFolderProjectIdAsync(Guid folderId, CancellationToken ct) =>
        db.Folders.AsNoTracking().Where(f => f.Id == folderId).Select(f => (Guid?)f.ProjectId).FirstOrDefaultAsync(ct);
}

/// <summary>
/// Appends audit entries with a plain INSERT on its own connection, outside the request's
/// DbContext: entries survive even when the request's own changes are rolled back.
/// </summary>
internal sealed class SqlServerAuditLogWriter(SqlConnections connections) : IAuditLogWriter
{
    private const string Columns =
        "(Id, OccurredAt, ActorId, ActorEmail, Action, Succeeded, ResourceType, ResourceId, ProjectId, Details, IpAddress, UserAgent)";

    public async Task WriteAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connections.Write);
        await connection.OpenAsync(ct);

        // One multi-row INSERT; requests record a handful of entries (a big folder delete a few hundred),
        // so chunk to stay well under SQL Server's 2,100-parameter limit.
        foreach (var batch in entries.Chunk(150))
        {
            await using var command = connection.CreateCommand();
            var rows = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var e = batch[i];
                rows.Add($"(@id{i}, @at{i}, @actor{i}, @email{i}, @action{i}, @ok{i}, @rtype{i}, @rid{i}, @project{i}, @details{i}, @ip{i}, @ua{i})");

                var p = command.Parameters;
                p.Add($"@id{i}", SqlDbType.UniqueIdentifier).Value = e.Id;
                p.Add($"@at{i}", SqlDbType.DateTimeOffset).Value = e.OccurredAt;
                p.Add($"@actor{i}", SqlDbType.UniqueIdentifier).Value = (object?)e.ActorId ?? DBNull.Value;
                p.Add($"@email{i}", SqlDbType.NVarChar, 256).Value = (object?)e.ActorEmail ?? DBNull.Value;
                p.Add($"@action{i}", SqlDbType.NVarChar, 100).Value = e.Action;
                p.Add($"@ok{i}", SqlDbType.Bit).Value = e.Succeeded;
                p.Add($"@rtype{i}", SqlDbType.NVarChar, 50).Value = (object?)e.ResourceType ?? DBNull.Value;
                p.Add($"@rid{i}", SqlDbType.UniqueIdentifier).Value = (object?)e.ResourceId ?? DBNull.Value;
                p.Add($"@project{i}", SqlDbType.UniqueIdentifier).Value = (object?)e.ProjectId ?? DBNull.Value;
                p.Add($"@details{i}", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(e.Details); // converts to json
                p.Add($"@ip{i}", SqlDbType.NVarChar, 64).Value = (object?)e.IpAddress ?? DBNull.Value;
                p.Add($"@ua{i}", SqlDbType.NVarChar, 512).Value = (object?)e.UserAgent ?? DBNull.Value;
            }

            command.CommandText = $"INSERT INTO {Tables.AuditLog} {Columns} VALUES {string.Join(",\n", rows)};";
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
