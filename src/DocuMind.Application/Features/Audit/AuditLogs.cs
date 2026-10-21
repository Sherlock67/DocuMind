using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Audit;

public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorId,
    string? ActorEmail,
    string Action,
    bool Succeeded,
    string? ResourceType,
    Guid? ResourceId,
    Guid? ProjectId,
    IReadOnlyDictionary<string, string> Details,
    string? IpAddress);

/// <summary>
/// Reads the audit trail, newest first. Admins can read everything; a project Owner can read the
/// trail of their own project (so <see cref="ProjectId"/> is required for non-admins).
/// </summary>
public sealed record GetAuditLogsQuery(
    Guid? ProjectId = null,
    Guid? ActorId = null,
    string? Action = null,
    Guid? ResourceId = null,
    bool? Succeeded = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50) : IQuery<PagedResult<AuditEntryDto>>;

internal sealed class GetAuditLogsQueryValidator : AbstractValidator<GetAuditLogsQuery>
{
    public GetAuditLogsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        RuleFor(x => x.Action).MaximumLength(100);
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From).When(x => x.From is not null && x.To is not null);
    }
}

internal sealed class GetAuditLogsQueryHandler(IReadDbContext db, IAccessControl access, ICurrentUser user)
    : IQueryHandler<GetAuditLogsQuery, PagedResult<AuditEntryDto>>
{
    public async Task<PagedResult<AuditEntryDto>> Handle(GetAuditLogsQuery query, CancellationToken ct)
    {
        if (query.ProjectId is { } projectId)
            await access.RequireProjectAsync(projectId, ProjectRole.Owner, ct);
        else if (user.Role != UserRole.Admin)
            throw new ForbiddenException("Only Admins can read the global audit log. Pass projectId to read a project you own.");

        var entries = db.AuditEntries;
        if (query.ProjectId is { } p) entries = entries.Where(e => e.ProjectId == p);
        if (query.ActorId is { } a) entries = entries.Where(e => e.ActorId == a);
        if (!string.IsNullOrWhiteSpace(query.Action)) entries = entries.Where(e => e.Action == query.Action);
        if (query.ResourceId is { } r) entries = entries.Where(e => e.ResourceId == r);
        if (query.Succeeded is { } s) entries = entries.Where(e => e.Succeeded == s);
        if (query.From is { } from) entries = entries.Where(e => e.OccurredAt >= from);
        if (query.To is { } to) entries = entries.Where(e => e.OccurredAt <= to);

        var total = await entries.CountAsync(ct);
        var page = await entries
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        var items = page.Select(e => new AuditEntryDto(
            e.Id, e.OccurredAt, e.ActorId, e.ActorEmail, e.Action, e.Succeeded,
            e.ResourceType, e.ResourceId, e.ProjectId, e.Details, e.IpAddress)).ToList();

        return new PagedResult<AuditEntryDto>(items, query.Page, query.PageSize, total);
    }
}
