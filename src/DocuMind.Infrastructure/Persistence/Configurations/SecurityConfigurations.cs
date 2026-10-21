using System.Text.Json;
using DocuMind.Domain.Auditing;
using DocuMind.Domain.Chat;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Infrastructure.Persistence.Configurations;

internal sealed class ProjectMemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    public void Configure(EntityTypeBuilder<ProjectMember> builder)
    {
        builder.ToTable(Tables.ProjectMembers);
        builder.HasKey(m => new { m.ProjectId, m.UserId });

        // "Which projects can this user read?" is the hottest authorization query.
        builder.HasIndex(m => new { m.UserId, m.ProjectId });
    }
}

internal sealed class CollectionShareConfiguration : IEntityTypeConfiguration<CollectionShare>
{
    public void Configure(EntityTypeBuilder<CollectionShare> builder)
    {
        builder.ToTable(Tables.CollectionShares);
        builder.HasKey(s => new { s.CollectionId, s.UserId });
        builder.HasIndex(s => new { s.UserId, s.CollectionId });
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable(Tables.AuditLog);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Action).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ActorEmail).HasMaxLength(256);
        builder.Property(e => e.ResourceType).HasMaxLength(50);
        builder.Property(e => e.IpAddress).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(512);

        // SQL Server 2025 native JSON type.
        builder.Property(e => e.Details)
            .HasColumnType("json")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                    v => new Dictionary<string, string>(v)));

        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.ProjectId, e.OccurredAt });
        builder.HasIndex(e => new { e.ActorId, e.OccurredAt });
        builder.HasIndex(e => new { e.ResourceId, e.OccurredAt });
        builder.HasIndex(e => new { e.Action, e.OccurredAt });
    }
}

/// <summary>
/// Write-model-only mapping: Identity tables, sessions, and foreign keys from domain tables to
/// users. The read model never needs the users table, so it stays out of <see cref="ReadDbContext"/>.
/// </summary>
internal static class IdentityModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUser>(b =>
        {
            b.ToTable(Tables.Users);
            b.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            b.HasIndex(u => new { u.Role, u.IsActive });
        });
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable(Tables.UserClaims);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable(Tables.UserLogins);
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable(Tables.UserTokens);

        modelBuilder.Entity<UserSession>(b =>
        {
            b.ToTable(Tables.UserSessions);
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(s => s.PreviousTokenHash).HasMaxLength(64);
            b.Property(s => s.RevokedReason).HasMaxLength(50);
            b.Property(s => s.IpAddress).HasMaxLength(64);
            b.Property(s => s.UserAgent).HasMaxLength(512);
            b.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(s => new { s.UserId, s.RevokedAt });
        });

        // Users are deactivated, never deleted, so Restrict keeps ownership and history intact.
        modelBuilder.Entity<ProjectMember>().HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CollectionShare>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Collection>().HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Document>().HasOne<ApplicationUser>().WithMany().HasForeignKey(d => d.UploadedBy).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ChatSession>().HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
    }
}
