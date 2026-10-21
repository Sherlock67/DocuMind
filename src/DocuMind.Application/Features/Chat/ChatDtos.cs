using DocuMind.Domain.Chat;

namespace DocuMind.Application.Features.Chat;

public sealed record CitationDto(int SourceNumber, Guid DocumentId, string FileName, Guid ChunkId, int? PageNumber, string Snippet, double Score)
{
    public static CitationDto From(Citation c) => new(c.SourceNumber, c.DocumentId, c.FileName, c.ChunkId, c.PageNumber, c.Snippet, c.Score);
}

public sealed record ChatMessageDto(Guid Id, ChatRole Role, string Content, IReadOnlyList<CitationDto> Citations, DateTimeOffset CreatedAt);

public sealed record ChatSessionSummaryDto(Guid Id, Guid? ProjectId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ChatSessionDto(Guid Id, Guid? ProjectId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChatMessageDto> Messages);

public sealed record AnswerDto(Guid MessageId, string Answer, IReadOnlyList<CitationDto> Citations);

/// <summary>Server-sent events emitted while an answer streams. <see cref="Type"/> becomes the SSE event name.</summary>
public abstract record ChatStreamEvent(string Type);

/// <summary>Sent first: every passage handed to the model, so the UI can render source cards immediately.</summary>
public sealed record SourcesEvent(IReadOnlyList<CitationDto> Sources) : ChatStreamEvent("sources");

public sealed record DeltaEvent(string Text) : ChatStreamEvent("delta");

/// <summary>Sent last, after the exchange is persisted. Lists only the sources the answer actually cited.</summary>
public sealed record CompletedEvent(Guid MessageId, IReadOnlyList<int> CitedSources) : ChatStreamEvent("done");
