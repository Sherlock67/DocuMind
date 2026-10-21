using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Models;
using DocuMind.Domain.Chat;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Chat.Queries;

public sealed record GetChatSessionsQuery(Guid? ProjectId = null, int Page = 1, int PageSize = 20) : IQuery<PagedResult<ChatSessionSummaryDto>>;

internal sealed class GetChatSessionsQueryValidator : AbstractValidator<GetChatSessionsQuery>
{
    public GetChatSessionsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

/// <summary>The caller's own conversations. Conversations are never visible to other users, Admins included.</summary>
internal sealed class GetChatSessionsQueryHandler(IReadDbContext db, ICurrentUser user)
    : IQueryHandler<GetChatSessionsQuery, PagedResult<ChatSessionSummaryDto>>
{
    public async Task<PagedResult<ChatSessionSummaryDto>> Handle(GetChatSessionsQuery query, CancellationToken ct)
    {
        var userId = user.Id;
        var sessions = db.ChatSessions.Where(s => s.OwnerId == userId);
        if (query.ProjectId is { } projectId)
            sessions = sessions.Where(s => s.ProjectId == projectId);

        var total = await sessions.CountAsync(ct);
        var items = await sessions
            .OrderByDescending(s => s.UpdatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(s => new ChatSessionSummaryDto(s.Id, s.ProjectId, s.Title, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return new PagedResult<ChatSessionSummaryDto>(items, query.Page, query.PageSize, total);
    }
}

public sealed record GetChatSessionQuery(Guid SessionId) : IQuery<ChatSessionDto>;

internal sealed class GetChatSessionQueryHandler(IReadDbContext db, ICurrentUser user) : IQueryHandler<GetChatSessionQuery, ChatSessionDto>
{
    public async Task<ChatSessionDto> Handle(GetChatSessionQuery query, CancellationToken ct)
    {
        var userId = user.Id;
        var session = await db.ChatSessions
            .Where(s => s.Id == query.SessionId && s.OwnerId == userId)
            .Select(s => new { s.Id, s.ProjectId, s.Title, s.CreatedAt, s.UpdatedAt })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(nameof(ChatSession), query.SessionId);

        var messages = await db.ChatMessages
            .Where(m => m.SessionId == query.SessionId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Role)
            .ToListAsync(ct);

        return new ChatSessionDto(
            session.Id, session.ProjectId, session.Title, session.CreatedAt, session.UpdatedAt,
            messages.Select(m => new ChatMessageDto(m.Id, m.Role, m.Content, m.Citations.Select(CitationDto.From).ToList(), m.CreatedAt)).ToList());
    }
}
