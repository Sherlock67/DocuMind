using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable(Tables.Documents);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.FileName).HasMaxLength(Document.MaxFileNameLength).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(255).IsRequired();
        builder.Property(d => d.StoragePath).HasMaxLength(1024).IsRequired();
        builder.Property(d => d.ContentHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(d => d.FailureReason).HasMaxLength(2000);

        builder.Ignore(d => d.Extension);

        builder.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Folder>().WithMany().HasForeignKey(d => d.FolderId).OnDelete(DeleteBehavior.Restrict);

        // Duplicate detection is per project: the same file may legitimately live in two projects.
        builder.HasIndex(d => new { d.ProjectId, d.ContentHash }).IsUnique();
        builder.HasIndex(d => new { d.ProjectId, d.FolderId, d.UploadedAt });
        builder.HasIndex(d => new { d.Status, d.UploadedAt });

        builder.HasMany(d => d.Chunks)
            .WithOne()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(d => d.Chunks).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(d => d.Pages)
            .WithOne()
            .HasForeignKey(p => p.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(d => d.Pages).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class DocumentPageConfiguration : IEntityTypeConfiguration<DocumentPage>
{
    public void Configure(EntityTypeBuilder<DocumentPage> builder)
    {
        builder.ToTable(Tables.DocumentPages);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Text).IsRequired();
        builder.HasIndex(p => new { p.DocumentId, p.SectionIndex }).IsUnique();
    }
}

internal sealed class DocumentChunkConfiguration(int embeddingDimensions) : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable(Tables.DocumentChunks);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Content).IsRequired();

        // Domain keeps a plain float[]; SQL Server 2025 stores it in a native VECTOR column.
        // No vector index: DiskANN indexes are not available on Express/LocalDB, so similarity
        // search is an exact scan (see SqlChunkSearchService).
        builder.Property(c => c.Embedding)
            .HasColumnType($"vector({embeddingDimensions})")
            .HasConversion(
                v => new SqlVector<float>(v),
                v => v.Memory.ToArray(),
                new ValueComparer<float[]>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Length,
                    v => v.ToArray()));

        builder.HasIndex(c => new { c.DocumentId, c.ChunkIndex }).IsUnique();

        builder.HasMany(c => c.Terms)
            .WithOne()
            .HasForeignKey(t => t.ChunkId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.Terms).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>
/// The keyword (inverted) index behind BM25 search. Clustered on (Term, ChunkId) so a query term
/// reads one contiguous range; the ChunkId index serves cascade deletes.
/// </summary>
internal sealed class ChunkTermConfiguration : IEntityTypeConfiguration<ChunkTerm>
{
    public void Configure(EntityTypeBuilder<ChunkTerm> builder)
    {
        builder.ToTable(Tables.DocumentChunkTerms);
        builder.HasKey(t => new { t.Term, t.ChunkId });
        builder.Property(t => t.Term).HasMaxLength(ChunkTerm.MaxTermLength);
        builder.HasIndex(t => t.ChunkId);
    }
}
