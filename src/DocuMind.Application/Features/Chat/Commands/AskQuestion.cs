using System.Text;
using DocuMind.Application.Abstractions.Messaging;
using FluentValidation;

namespace DocuMind.Application.Features.Chat.Commands;

public interface IQuestionRequest
{
    Guid SessionId { get; }
    string Question { get; }
    Guid? FolderId { get; }
    Guid? CollectionId { get; }
    IReadOnlyList<Guid>? DocumentIds { get; }
}

/// <summary>Asks a question and waits for the complete, cited answer.</summary>
public sealed record AskQuestionCommand(
    Guid SessionId,
    string Question,
    Guid? FolderId = null,
    Guid? CollectionId = null,
    IReadOnlyList<Guid>? DocumentIds = null)
    : ICommand<AnswerDto>, IQuestionRequest;

/// <summary>
/// Asks a question and returns the answer as a stream of events. Validation, the session
/// lookup and retrieval run when the command is handled; tokens flow as the stream is read.
/// </summary>
public sealed record StreamAnswerCommand(
    Guid SessionId,
    string Question,
    Guid? FolderId = null,
    Guid? CollectionId = null,
    IReadOnlyList<Guid>? DocumentIds = null)
    : ICommand<IAsyncEnumerable<ChatStreamEvent>>, IQuestionRequest;

internal abstract class QuestionRequestValidator<T> : AbstractValidator<T> where T : IQuestionRequest
{
    protected QuestionRequestValidator()
    {
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.Question).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.DocumentIds).Must(ids => ids is null || ids.Count <= 100)
            .WithMessage("At most 100 documents can be selected.");
    }
}

internal sealed class AskQuestionCommandValidator : QuestionRequestValidator<AskQuestionCommand>;

internal sealed class StreamAnswerCommandValidator : QuestionRequestValidator<StreamAnswerCommand>;

internal sealed class AskQuestionCommandHandler(RagChatService rag) : ICommandHandler<AskQuestionCommand, AnswerDto>
{
    public async Task<AnswerDto> Handle(AskQuestionCommand command, CancellationToken ct)
    {
        var prepared = await rag.PrepareAsync(command, ct);

        var answer = new StringBuilder();
        CompletedEvent? completed = null;
        await foreach (var evt in rag.StreamAsync(prepared, ct))
        {
            switch (evt)
            {
                case DeltaEvent d: answer.Append(d.Text); break;
                case CompletedEvent c: completed = c; break;
            }
        }

        var citations = prepared.Sources
            .Where(s => completed!.CitedSources.Contains(s.SourceNumber))
            .Select(CitationDto.From)
            .ToList();

        return new AnswerDto(completed!.MessageId, answer.ToString(), citations);
    }
}

internal sealed class StreamAnswerCommandHandler(RagChatService rag)
    : ICommandHandler<StreamAnswerCommand, IAsyncEnumerable<ChatStreamEvent>>
{
    public async Task<IAsyncEnumerable<ChatStreamEvent>> Handle(StreamAnswerCommand command, CancellationToken ct)
    {
        var prepared = await rag.PrepareAsync(command, ct);
        return rag.StreamAsync(prepared, ct);
    }
}
