using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using FluentValidation;

namespace DocuMind.Application.Features.Auth;

// ----- Requests ---------------------------------------------------------------------------------

public sealed record RegisterCommand(string Email, string Password, string DisplayName) : ICommand<UserDto>, IPublicRequest;

public sealed record LoginCommand(string Email, string Password) : ICommand<AuthTokens>, IPublicRequest;

public sealed record RefreshTokenCommand(string RefreshToken) : ICommand<AuthTokens>, IPublicRequest;

/// <summary>Ends the current session: its refresh token stops working and its access token is rejected.</summary>
public sealed record LogoutCommand : ICommand<Unit>;

public sealed record LogoutEverywhereCommand : ICommand<int>;

public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : ICommand<Unit>;

public sealed record GetCurrentUserQuery : IQuery<UserDto>;

// ----- Validation -------------------------------------------------------------------------------

internal sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256); // strength rules are enforced by Identity
    }
}

internal sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

internal sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(512);
}

internal sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(256).NotEqual(x => x.CurrentPassword)
            .WithMessage("The new password must differ from the current one.");
    }
}

// ----- Handlers ---------------------------------------------------------------------------------

internal sealed class RegisterCommandHandler(IIdentityService identity, IAuditTrail audit) : ICommandHandler<RegisterCommand, UserDto>
{
    public async Task<UserDto> Handle(RegisterCommand command, CancellationToken ct)
    {
        var user = await identity.RegisterAsync(command.Email.Trim(), command.Password, command.DisplayName.Trim(), ct);
        audit.Record(AuditActions.UserRegistered, "user", user.Id, actorId: user.Id, actorEmail: user.Email,
            details: new Dictionary<string, string> { ["role"] = user.Role.ToString() });
        return user;
    }
}

internal sealed class LoginCommandHandler(IIdentityService identity, ICurrentUser caller, IAuditTrail audit) : ICommandHandler<LoginCommand, AuthTokens>
{
    public async Task<AuthTokens> Handle(LoginCommand command, CancellationToken ct)
    {
        var email = command.Email.Trim();
        try
        {
            var tokens = await identity.LoginAsync(email, command.Password, new ClientInfo(caller.IpAddress, caller.UserAgent), ct);
            audit.Record(AuditActions.LoginSucceeded, "user", tokens.User.Id, actorId: tokens.User.Id, actorEmail: tokens.User.Email);
            return tokens;
        }
        catch (AuthenticationFailedException ex)
        {
            audit.Record(AuditActions.LoginFailed, "user", ex.UserId, succeeded: false, actorId: ex.UserId, actorEmail: email,
                details: new Dictionary<string, string> { ["reason"] = ex.AuditReason ?? "invalid_credentials" });
            throw;
        }
    }
}

internal sealed class RefreshTokenCommandHandler(IIdentityService identity, ICurrentUser caller, IAuditTrail audit)
    : ICommandHandler<RefreshTokenCommand, AuthTokens>
{
    public async Task<AuthTokens> Handle(RefreshTokenCommand command, CancellationToken ct)
    {
        try
        {
            return await identity.RefreshAsync(command.RefreshToken, new ClientInfo(caller.IpAddress, caller.UserAgent), ct);
        }
        catch (AuthenticationFailedException ex) when (ex.AuditReason == "refresh_token_reused")
        {
            // A rotated token came back: someone else may hold a copy. The session has been revoked.
            audit.Record(AuditActions.RefreshTokenReused, "user", ex.UserId, succeeded: false, actorId: ex.UserId);
            throw;
        }
    }
}

internal sealed class LogoutCommandHandler(IIdentityService identity, ICurrentUser user, IAuditTrail audit) : ICommandHandler<LogoutCommand, Unit>
{
    public async Task<Unit> Handle(LogoutCommand command, CancellationToken ct)
    {
        if (user.SessionId is { } sessionId)
            await identity.LogoutAsync(user.Id, sessionId, ct);

        audit.Record(AuditActions.LoggedOut, "user", user.Id);
        return Unit.Value;
    }
}

internal sealed class LogoutEverywhereCommandHandler(IIdentityService identity, ICurrentUser user, IAuditTrail audit)
    : ICommandHandler<LogoutEverywhereCommand, int>
{
    public async Task<int> Handle(LogoutEverywhereCommand command, CancellationToken ct)
    {
        var revoked = await identity.LogoutEverywhereAsync(user.Id, ct);
        audit.Record(AuditActions.LoggedOutEverywhere, "user", user.Id, details: new Dictionary<string, string> { ["sessions"] = revoked.ToString() });
        return revoked;
    }
}

internal sealed class ChangePasswordCommandHandler(IIdentityService identity, ICurrentUser user, IAuditTrail audit)
    : ICommandHandler<ChangePasswordCommand, Unit>
{
    public async Task<Unit> Handle(ChangePasswordCommand command, CancellationToken ct)
    {
        await identity.ChangePasswordAsync(user.Id, user.SessionId ?? Guid.Empty, command.CurrentPassword, command.NewPassword, ct);
        audit.Record(AuditActions.PasswordChanged, "user", user.Id);
        return Unit.Value;
    }
}

internal sealed class GetCurrentUserQueryHandler(IUserDirectory users, ICurrentUser user) : IQueryHandler<GetCurrentUserQuery, UserDto>
{
    public async Task<UserDto> Handle(GetCurrentUserQuery query, CancellationToken ct) =>
        await users.GetByIdAsync(user.Id, ct) ?? throw new AuthenticationFailedException();
}
