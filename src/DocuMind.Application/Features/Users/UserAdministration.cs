using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Common.Security;
using DocuMind.Domain.Common;
using DocuMind.Domain.Security;
using FluentValidation;

namespace DocuMind.Application.Features.Users;

[RequireRole(UserRole.Admin)]
public sealed record GetUsersQuery(int Page = 1, int PageSize = 20, string? Search = null) : IQuery<PagedResult<UserDto>>;

/// <summary>Creates an account directly (useful when self-registration is disabled).</summary>
[RequireRole(UserRole.Admin)]
public sealed record CreateUserCommand(string Email, string Password, string DisplayName, UserRole Role) : ICommand<UserDto>;

[RequireRole(UserRole.Admin)]
public sealed record ChangeUserRoleCommand(Guid UserId, UserRole Role) : ICommand<Unit>;

/// <summary>Deactivating signs the user out everywhere and blocks future logins; their data is kept.</summary>
[RequireRole(UserRole.Admin)]
public sealed record SetUserActiveCommand(Guid UserId, bool IsActive) : ICommand<Unit>;

internal sealed class GetUsersQueryValidator : AbstractValidator<GetUsersQuery>
{
    public GetUsersQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(200);
    }
}

internal sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Role).IsInEnum();
    }
}

internal sealed class ChangeUserRoleCommandValidator : AbstractValidator<ChangeUserRoleCommand>
{
    public ChangeUserRoleCommandValidator() => RuleFor(x => x.Role).IsInEnum();
}

internal sealed class GetUsersQueryHandler(IUserDirectory users) : IQueryHandler<GetUsersQuery, PagedResult<UserDto>>
{
    public Task<PagedResult<UserDto>> Handle(GetUsersQuery query, CancellationToken ct) =>
        users.ListAsync(query.Page, query.PageSize, query.Search, ct);
}

internal sealed class CreateUserCommandHandler(IIdentityService identity, IAuditTrail audit) : ICommandHandler<CreateUserCommand, UserDto>
{
    public async Task<UserDto> Handle(CreateUserCommand command, CancellationToken ct)
    {
        var user = await identity.CreateUserAsync(command.Email.Trim(), command.Password, command.DisplayName.Trim(), command.Role, ct);

        audit.Record(AuditActions.UserRegistered, "user", user.Id,
            details: new Dictionary<string, string> { ["email"] = user.Email, ["role"] = user.Role.ToString(), ["createdByAdmin"] = "true" });
        return user;
    }
}

internal sealed class ChangeUserRoleCommandHandler(IIdentityService identity, IUserDirectory users, ICurrentUser caller, IAuditTrail audit)
    : ICommandHandler<ChangeUserRoleCommand, Unit>
{
    public async Task<Unit> Handle(ChangeUserRoleCommand command, CancellationToken ct)
    {
        var target = await users.GetByIdAsync(command.UserId, ct) ?? throw new NotFoundException("User", command.UserId);
        if (target.Role == command.Role)
            return Unit.Value;

        if (target.Role == UserRole.Admin && target.IsActive && await users.CountActiveAdminsAsync(ct) <= 1)
            throw new DomainException("The last active Admin cannot be demoted.");

        await identity.SetRoleAsync(target.Id, command.Role, ct);
        audit.Record(AuditActions.UserRoleChanged, "user", target.Id, details: new Dictionary<string, string>
        {
            ["email"] = target.Email,
            ["from"] = target.Role.ToString(),
            ["to"] = command.Role.ToString(),
            ["self"] = (target.Id == caller.Id).ToString(),
        });
        return Unit.Value;
    }
}

internal sealed class SetUserActiveCommandHandler(IIdentityService identity, IUserDirectory users, ICurrentUser caller, IAuditTrail audit)
    : ICommandHandler<SetUserActiveCommand, Unit>
{
    public async Task<Unit> Handle(SetUserActiveCommand command, CancellationToken ct)
    {
        var target = await users.GetByIdAsync(command.UserId, ct) ?? throw new NotFoundException("User", command.UserId);
        if (target.IsActive == command.IsActive)
            return Unit.Value;

        if (!command.IsActive)
        {
            if (target.Id == caller.Id)
                throw new DomainException("You cannot deactivate your own account.");
            if (target.Role == UserRole.Admin && await users.CountActiveAdminsAsync(ct) <= 1)
                throw new DomainException("The last active Admin cannot be deactivated.");
        }

        await identity.SetActiveAsync(target.Id, command.IsActive, ct);
        audit.Record(command.IsActive ? AuditActions.UserActivated : AuditActions.UserDeactivated, "user", target.Id,
            details: new Dictionary<string, string> { ["email"] = target.Email });
        return Unit.Value;
    }
}
