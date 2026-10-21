using DocuMind.Domain.Common;

namespace DocuMind.Domain.Chat;

/// <summary>A conversation with the assistant. Questions and answers are always appended as a pair.</summary>
public sealed class ChatSession : Entity, IAggregateRoot
{
    private readonly List<ChatMessage> _messages = [];

    private ChatSession() { } // EF Core

    /// <summary>Conversations are personal: only their owner can read or continue them.</summary>
    public Guid OwnerId { get; private set; }

    /// <summary>When set, answers in this conversation only draw on that project's documents.</summary>
    public Guid? ProjectId { get; private set; }

    public string Title { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<ChatMessage> Messages => _messages.AsReadOnly();

    public static ChatSession Start(Guid ownerId, Guid? projectId, string? title, DateTimeOffset now) => new()
    {
        OwnerId = ownerId,
        ProjectId = projectId,
        Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Records one question/answer exchange. Appending both together keeps the history
    /// strictly alternating user/assistant, which the LLM API requires.
    /// </summary>
    public ChatMessage RecordExchange(string question, string answer, IReadOnlyList<Citation> citations, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new DomainException("Question is required.");

        if (_messages.Count == 0 && Title == "New conversation")
            Title = question.Length <= 80 ? question : question[..77] + "...";

        _messages.Add(ChatMessage.Create(Id, ChatRole.User, question, [], now));
        var reply = ChatMessage.Create(Id, ChatRole.Assistant, answer, citations, now);
        _messages.Add(reply);
        UpdatedAt = now;
        return reply;
    }
}
