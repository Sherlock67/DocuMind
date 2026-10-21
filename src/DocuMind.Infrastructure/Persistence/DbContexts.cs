using DocuMind.Application.Abstractions.Data;
using DocuMind.Domain.Auditing;
using DocuMind.Domain.Chat;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Infrastructure.AI;
using DocuMind.Infrastructure.Identity;
using DocuMind.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocuMind.Infrastructure.Persistence;

/// <summary>Model shared by both contexts so the read side can never drift from the write side.</summary>
internal static class DocuMindModel
{
    public static void Configure(ModelBuilder modelBuilder, int embeddingDimensions)
    {
        modelBuilder.ApplyConfiguration(new ProjectConfiguration());
        modelBuilder.ApplyConfiguration(new ProjectMemberConfiguration());
        modelBuilder.ApplyConfiguration(new CollectionShareConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new FolderConfiguration());
        modelBuilder.ApplyConfiguration(new CollectionConfiguration());
        modelBuilder.ApplyConfiguration(new CollectionItemConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentPageConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentChunkConfiguration(embeddingDimensions));
        modelBuilder.ApplyConfiguration(new ChunkTermConfiguration());
        modelBuilder.ApplyConfiguration(new ChatSessionConfiguration());
        modelBuilder.ApplyConfiguration(new ChatMessageConfiguration());
    }
}

/// <summary>Write model: change tracking, Identity stores, owns migrations, implements the unit of work.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IOptions<EmbeddingOptions> embedding)
    : IdentityUserContext<ApplicationUser, Guid>(options), IUnitOfWork
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity tables
        DocuMindModel.Configure(modelBuilder, embedding.Value.Dimensions);
        IdentityModelConfiguration.Configure(modelBuilder);
    }
}

/// <summary>
/// Read model: no tracking, optionally pointed at a readable secondary (ConnectionStrings:SqlServerRead,
/// e.g. an Always On secondary with ApplicationIntent=ReadOnly).
/// Query handlers only ever see this through <see cref="IReadDbContext"/>.
/// </summary>
public sealed class ReadDbContext(DbContextOptions<ReadDbContext> options, IOptions<EmbeddingOptions> embedding)
    : DbContext(options), IReadDbContext
{
    public IQueryable<Project> Projects => Set<Project>();
    public IQueryable<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public IQueryable<Folder> Folders => Set<Folder>();
    public IQueryable<Collection> Collections => Set<Collection>();
    public IQueryable<CollectionItem> CollectionItems => Set<CollectionItem>();
    public IQueryable<CollectionShare> CollectionShares => Set<CollectionShare>();
    public IQueryable<AuditEntry> AuditEntries => Set<AuditEntry>();
    public IQueryable<Document> Documents => Set<Document>();
    public IQueryable<DocumentPage> DocumentPages => Set<DocumentPage>();
    public IQueryable<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public IQueryable<ChatSession> ChatSessions => Set<ChatSession>();
    public IQueryable<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        DocuMindModel.Configure(modelBuilder, embedding.Value.Dimensions);

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("The read context is read-only.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The read context is read-only.");
}
