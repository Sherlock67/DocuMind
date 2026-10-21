using DocuMind.Domain.Common;

namespace DocuMind.Domain.Chat;

public enum ChatRole
{
    User = 0,
    Assistant = 1,
}

public sealed class ChatMessage : Entity
{
    private ChatMessage() { } // EF Core

    public Guid SessionId { get; private set; }
    public ChatRole Role { get; private set; }
    public string Content { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Sources the assistant used for this answer. Empty for user messages.</summary>
    public List<Citation> Citations { get; private set; } = [];

    internal static ChatMessage Create(Guid sessionId, ChatRole role, string content, IReadOnlyList<Citation> citations, DateTimeOffset now) => new()
    {
        SessionId = sessionId,
        Role = role,
        Content = content,
        Citations = [.. citations],
        CreatedAt = now,
    };
}

/// <summary>Value object pointing at the chunk an answer was grounded on.</summary>
public sealed record Citation(
    int SourceNumber,
    Guid DocumentId,
    string FileName,
    Guid ChunkId,
    int? PageNumber,
    string Snippet,
    double Score);
