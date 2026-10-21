using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Infrastructure.Persistence.Configurations;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable(Tables.Projects);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(Project.MaxNameLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Project.MaxDescriptionLength);
        builder.HasIndex(p => p.UpdatedAt);

        builder.HasMany(p => p.Members).WithOne().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable(Tables.Folders);
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Name).HasMaxLength(Folder.MaxNameLength).IsRequired();

        builder.HasOne<Project>().WithMany().HasForeignKey(f => f.ProjectId).OnDelete(DeleteBehavior.Cascade);

        // SQL Server rejects self-referencing ON DELETE CASCADE ("may cause cycles"), so the database
        // uses NO ACTION and the app deletes subtrees itself (DeleteFolderCommandHandler removes every
        // descendant). Deleting a project still works: its cascade removes all folders in one statement.
        builder.HasOne<Folder>().WithMany().HasForeignKey(f => f.ParentId).OnDelete(DeleteBehavior.ClientCascade);

        // Sibling names are unique, root folders included. SQL Server unique indexes already treat
        // NULLs as equal; HasFilter(null) stops EF adding its default "WHERE ParentId IS NOT NULL".
        builder.HasIndex(f => new { f.ProjectId, f.ParentId, f.Name }).IsUnique().HasFilter(null);
    }
}

internal sealed class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable(Tables.Collections);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(Collection.MaxNameLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(Collection.MaxDescriptionLength);

        builder.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.ProjectId, c.Name }).IsUnique();

        builder.HasMany(c => c.Items).WithOne().HasForeignKey(i => i.CollectionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(c => c.Shares).WithOne().HasForeignKey(s => s.CollectionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Shares).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(c => c.OwnerId);
    }
}

internal sealed class CollectionItemConfiguration : IEntityTypeConfiguration<CollectionItem>
{
    public void Configure(EntityTypeBuilder<CollectionItem> builder)
    {
        builder.ToTable(Tables.CollectionDocuments);
        builder.HasKey(i => new { i.CollectionId, i.DocumentId });

        // Deleting a document silently drops it from every collection.
        builder.HasOne<Document>().WithMany().HasForeignKey(i => i.DocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => i.DocumentId);
    }
}
