using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Chat;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;

namespace DocuMind.Application.Features.Chat.Commands;

/// <summary>Starts a conversation, optionally bound to a project so answers only use that project's documents.</summary>
public sealed record CreateChatSessionCommand(Guid? ProjectId = null, string? Title = null) : ICommand<ChatSessionSummaryDto>;

internal sealed class CreateChatSessionCommandValidator : AbstractValidator<CreateChatSessionCommand>
{
    public CreateChatSessionCommandValidator() => RuleFor(x => x.Title).MaximumLength(200);
}

internal sealed class CreateChatSessionCommandHandler(
    IChatSessionRepository sessions,
    IAccessControl access,
    ICurrentUser user,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<CreateChatSessionCommand, ChatSessionSummaryDto>
{
    public async Task<ChatSessionSummaryDto> Handle(CreateChatSessionCommand command, CancellationToken ct)
    {
        if (command.ProjectId is { } projectId)
            await access.RequireProjectAsync(projectId, ProjectRole.Viewer, ct);

        var session = ChatSession.Start(user.Id, command.ProjectId, command.Title, clock.GetUtcNow());
        sessions.Add(session);
        await unitOfWork.SaveChangesAsync(ct);
        return new ChatSessionSummaryDto(session.Id, session.ProjectId, session.Title, session.CreatedAt, session.UpdatedAt);
    }
}
