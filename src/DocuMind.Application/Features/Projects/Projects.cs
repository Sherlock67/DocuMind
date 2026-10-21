using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Common;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.Application.Features.Projects;

public sealed record StatusCountsDto(int Queued, int Processing, int Completed, int Failed)
{
    public int Total => Queued + Processing + Completed + Failed;

    public static readonly StatusCountsDto Empty = new(0, 0, 0, 0);
}

/// <param name="MyRole">The caller's role in the project; null for an Admin who is not a member.</param>
public sealed record ProjectDto(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ProjectRole? MyRole,
    StatusCountsDto Documents,
    long TotalSizeBytes);

// ----- Commands ---------------------------------------------------------------------------------

/// <summary>Creates a project with the caller as its Owner. Managers and Admins only.</summary>
[RequireRole(UserRole.Manager)]
public sealed record CreateProjectCommand(string Name, string? Description = null) : ICommand<ProjectDto>;

public sealed record UpdateProjectCommand(Guid ProjectId, string Name, string? Description = null) : ICommand<Unit>;

/// <summary>
/// Deletes a project (Owner only). Without <see cref="Force"/> the project must have no documents;
/// with it, every document (rows, chunks and stored files), folder, collection and project chat goes too.
/// </summary>
public sealed record DeleteProjectCommand(Guid ProjectId, bool Force = false) : ICommand<Unit>;

internal sealed class CreateProjectCommandValidator : AbstractValidator<CreateProjectCommand>
{
    public CreateProjectCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Project.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(Project.MaxDescriptionLength);
    }
}

internal sealed class UpdateProjectCommandValidator : AbstractValidator<UpdateProjectCommand>
{
    public UpdateProjectCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Project.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(Project.MaxDescriptionLength);
    }
}

internal sealed class CreateProjectCommandHandler(
    IProjectRepository projects,
    IUnitOfWork unitOfWork,
    ICurrentUser user,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<CreateProjectCommand, ProjectDto>
{
    public async Task<ProjectDto> Handle(CreateProjectCommand command, CancellationToken ct)
    {
        var project = Project.Create(command.Name, command.Description, user.Id, clock.GetUtcNow());
        projects.Add(project);
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.ProjectCreated, "project", project.Id, project.Id, new Dictionary<string, string> { ["name"] = project.Name });
        return new ProjectDto(project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt,
            ProjectRole.Owner, StatusCountsDto.Empty, 0);
    }
}

internal sealed class UpdateProjectCommandHandler(
    IProjectRepository projects,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    TimeProvider clock) : ICommandHandler<UpdateProjectCommand, Unit>
{
    public async Task<Unit> Handle(UpdateProjectCommand command, CancellationToken ct)
    {
        await access.RequireProjectAsync(command.ProjectId, ProjectRole.Owner, ct);

        var project = await projects.GetByIdAsync(command.ProjectId, ct)
            ?? throw new NotFoundException(nameof(Project), command.ProjectId);

        var previousName = project.Name;
        project.Update(command.Name, command.Description, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);

        audit.Record(AuditActions.ProjectUpdated, "project", project.Id, project.Id,
            new Dictionary<string, string> { ["from"] = previousName, ["to"] = project.Name });
        return Unit.Value;
    }
}

internal sealed class DeleteProjectCommandHandler(
    IProjectRepository projects,
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IAccessControl access,
    IAuditTrail audit,
    DocumentFileCleanup cleanup) : ICommandHandler<DeleteProjectCommand, Unit>
{
    public async Task<Unit> Handle(DeleteProjectCommand command, CancellationToken ct)
    {
        await access.RequireProjectAsync(command.ProjectId, ProjectRole.Owner, ct);

        var project = await projects.GetByIdAsync(command.ProjectId, ct)
            ?? throw new NotFoundException(nameof(Project), command.ProjectId);

        IReadOnlyList<Document> doomed = [];
        if (command.Force)
        {
            doomed = await documents.ListByProjectAsync(project.Id, ct);
            foreach (var document in doomed)
                documents.Remove(document);
        }
        else if (await documents.AnyInProjectAsync(project.Id, ct))
        {
            throw new DomainException("The project still contains documents. Delete them first or pass force=true.");
        }

        // Members, folders, collections and project chats cascade in the database.
        projects.Remove(project);
        await unitOfWork.SaveChangesAsync(ct);

        foreach (var document in doomed)
            audit.Record(AuditActions.DocumentDeleted, "document", document.Id, project.Id,
                new Dictionary<string, string> { ["fileName"] = document.FileName, ["via"] = "project.deleted" });
        audit.Record(AuditActions.ProjectDeleted, "project", project.Id, project.Id,
            new Dictionary<string, string> { ["name"] = project.Name, ["documentsDeleted"] = doomed.Count.ToString() });

        await cleanup.DeleteFilesAsync(doomed.Select(d => d.StoragePath), ct);
        return Unit.Value;
    }
}

// ----- Queries ----------------------------------------------------------------------------------

/// <summary>Projects the caller is a member of (every project for Admins).</summary>
public sealed record GetProjectsQuery(int Page = 1, int PageSize = 20, string? Search = null) : IQuery<PagedResult<ProjectDto>>;

public sealed record GetProjectByIdQuery(Guid ProjectId) : IQuery<ProjectDto>;

internal sealed class GetProjectsQueryValidator : AbstractValidator<GetProjectsQuery>
{
    public GetProjectsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(200);
    }
}

internal sealed class GetProjectsQueryHandler(IReadDbContext db, IAccessControl access)
    : IQueryHandler<GetProjectsQuery, PagedResult<ProjectDto>>
{
    public async Task<PagedResult<ProjectDto>> Handle(GetProjectsQuery query, CancellationToken ct)
    {
        var principal = access.Principal;
        var projects = db.ReadableProjects(principal);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            projects = projects.Where(p => p.Name.ToLower().Contains(search));
        }

        var total = await projects.CountAsync(ct);
        var page = await projects
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        var ids = page.Select(p => p.Id).ToList();
        var stats = await ProjectStats.LoadAsync(db, ids, ct);
        var roles = await db.ProjectMembers
            .Where(m => m.UserId == principal.UserId && ids.Contains(m.ProjectId))
            .ToDictionaryAsync(m => m.ProjectId, m => m.Role, ct);

        return new PagedResult<ProjectDto>(
            page.Select(p => ProjectStats.ToDto(p, roles.TryGetValue(p.Id, out var r) ? r : null, stats)).ToList(),
            query.Page, query.PageSize, total);
    }
}

internal sealed class GetProjectByIdQueryHandler(IReadDbContext db, IAccessControl access) : IQueryHandler<GetProjectByIdQuery, ProjectDto>
{
    public async Task<ProjectDto> Handle(GetProjectByIdQuery query, CancellationToken ct)
    {
        await access.RequireProjectAsync(query.ProjectId, ProjectRole.Viewer, ct);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == query.ProjectId, ct)
            ?? throw new NotFoundException(nameof(Project), query.ProjectId);

        var userId = access.Principal.UserId;
        var role = await db.ProjectMembers
            .Where(m => m.ProjectId == project.Id && m.UserId == userId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(ct);

        return ProjectStats.ToDto(project, role, await ProjectStats.LoadAsync(db, [project.Id], ct));
    }
}

/// <summary>Per-project document counts by status and total size, in one grouped query.</summary>
internal static class ProjectStats
{
    public sealed record Row(Guid ProjectId, DocumentStatus Status, int Count, long Bytes);

    public static async Task<ILookup<Guid, Row>> LoadAsync(IReadDbContext db, IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        var rows = await db.Documents
            .Where(d => projectIds.Contains(d.ProjectId))
            .GroupBy(d => new { d.ProjectId, d.Status })
            .Select(g => new Row(g.Key.ProjectId, g.Key.Status, g.Count(), g.Sum(d => d.SizeBytes)))
            .ToListAsync(ct);

        return rows.ToLookup(r => r.ProjectId);
    }

    public static ProjectDto ToDto(Project p, ProjectRole? myRole, ILookup<Guid, Row> stats)
    {
        var rows = stats[p.Id].ToList();
        int Count(DocumentStatus s) => rows.Where(r => r.Status == s).Sum(r => r.Count);

        return new ProjectDto(
            p.Id, p.Name, p.Description, p.CreatedAt, p.UpdatedAt, myRole,
            new StatusCountsDto(Count(DocumentStatus.Queued), Count(DocumentStatus.Processing), Count(DocumentStatus.Completed), Count(DocumentStatus.Failed)),
            rows.Sum(r => r.Bytes));
    }
}
