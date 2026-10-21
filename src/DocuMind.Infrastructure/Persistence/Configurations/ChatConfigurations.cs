using DocuMind.Domain.Chat;
using DocuMind.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocuMind.Infrastructure.Persistence.Configurations;

internal sealed class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.ToTable(Tables.ChatSessions);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Title).HasMaxLength(200).IsRequired();
        builder.HasIndex(s => new { s.OwnerId, s.UpdatedAt });

        // Deleting a project deletes the conversations bound to it.
        builder.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(s => s.Messages)
            .WithOne()
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(s => s.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable(Tables.ChatMessages);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        // Stored as int on purpose: a question and its answer share a timestamp, and
        // ordering by (CreatedAt, Role) relies on User (0) sorting before Assistant (1).
        builder.Property(m => m.Role);
        builder.Property(m => m.Content).IsRequired();

        // Citations are a value-object list owned by the message: stored as one JSON column.
        builder.ComplexCollection(m => m.Citations, c => c.ToJson());

        builder.HasIndex(m => new { m.SessionId, m.CreatedAt });
    }
}
