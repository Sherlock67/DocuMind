using DocuMind.Api.Infrastructure;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Features.Audit;
using DocuMind.Application.Features.Auth;
using DocuMind.Application.Features.Users;
using DocuMind.Domain.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DocuMind.Api.Endpoints;

/// <summary>Authentication, user administration and the audit log.</summary>
internal static class SecurityEndpoints
{
    public sealed record RegisterRequest(string Email, string Password, string DisplayName);

    public sealed record LoginRequest(string Email, string Password);

    /// <summary>
    /// What browsers get back from login/refresh. The refresh token is deliberately absent: it travels
    /// only in an HttpOnly cookie, so page scripts (and therefore XSS) can never read it.
    /// </summary>
    public sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record CreateUserRequest(string Email, string Password, string DisplayName, UserRole Role);

    public sealed record RoleRequest(UserRole Role);

    public sealed record ActiveRequest(bool IsActive);

    public sealed record RevokedResponse(int SessionsRevoked);

    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting(SecuritySetup.AuthRateLimit)
            .WithSummary("Create an account. The first account ever created becomes Admin.");
        auth.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(SecuritySetup.AuthRateLimit)
            .WithSummary("Exchange email + password for an access token (15 min); the refresh token is set as an HttpOnly cookie.");
        auth.MapPost("/refresh", Refresh).AllowAnonymous().RequireRateLimiting(SecuritySetup.AuthRateLimit)
            .WithSummary("Use the refresh-token cookie to get a new access token. The cookie rotates; replaying an old one revokes the session.");
        auth.MapPost("/logout", Logout).WithSummary("End the current session.");
        auth.MapPost("/logout-all", LogoutAll).WithSummary("End every session of the current user.");
        auth.MapPost("/change-password", ChangePassword).WithSummary("Change password; other sessions are signed out.");
        auth.MapGet("/me", Me).WithSummary("The signed-in user.");

        var admin = app.MapGroup("/api/admin/users").WithTags("Admin").RequireAuthorization(SecuritySetup.AdminPolicy);
        admin.MapGet("/", ListUsers).WithSummary("List users.");
        admin.MapPost("/", CreateUser).WithSummary("Create a user with a given role.");
        admin.MapPut("/{id:guid}/role", ChangeRole).WithSummary("Change a user's system role (Admin, Manager, User).");
        admin.MapPut("/{id:guid}/active", SetActive).WithSummary("Activate or deactivate a user. Deactivation signs them out everywhere.");

        app.MapGet("/api/audit-logs", AuditLogs).WithTags("Audit")
            .WithSummary("Audit trail, newest first. Admins: everything. Project Owners: pass projectId.");

        return app;
    }

    private static async Task<Created<UserDto>> Register(RegisterRequest request, ISender sender, CancellationToken ct)
    {
        var user = await sender.Send(new RegisterCommand(request.Email, request.Password, request.DisplayName), ct);
        return TypedResults.Created("/api/auth/me", user);
    }

    private static async Task<Ok<AuthResponse>> Login(LoginRequest request, ISender sender, HttpContext http, CancellationToken ct) =>
        TypedResults.Ok(RefreshCookie.Issue(http, await sender.Send(new LoginCommand(request.Email, request.Password), ct)));

    private static async Task<Ok<AuthResponse>> Refresh(ISender sender, HttpContext http, CancellationToken ct)
    {
        var token = RefreshCookie.Read(http) ?? throw new AuthenticationFailedException("Not signed in.");
        try
        {
            return TypedResults.Ok(RefreshCookie.Issue(http, await sender.Send(new RefreshTokenCommand(token), ct)));
        }
        catch (AuthenticationFailedException)
        {
            RefreshCookie.Clear(http); // dead session: stop the browser from retrying with it
            throw;
        }
    }

    private static async Task<NoContent> Logout(ISender sender, HttpContext http, CancellationToken ct)
    {
        await sender.Send(new LogoutCommand(), ct);
        RefreshCookie.Clear(http);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<RevokedResponse>> LogoutAll(ISender sender, HttpContext http, CancellationToken ct)
    {
        var revoked = await sender.Send(new LogoutEverywhereCommand(), ct);
        RefreshCookie.Clear(http);
        return TypedResults.Ok(new RevokedResponse(revoked));
    }

    private static async Task<NoContent> ChangePassword(ChangePasswordRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new ChangePasswordCommand(request.CurrentPassword, request.NewPassword), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<UserDto>> Me(ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetCurrentUserQuery(), ct));

    private static async Task<Ok<PagedResult<UserDto>>> ListUsers(ISender sender, CancellationToken ct, int page = 1, int pageSize = 20, string? search = null) =>
        TypedResults.Ok(await sender.Send(new GetUsersQuery(page, pageSize, search), ct));

    private static async Task<Created<UserDto>> CreateUser(CreateUserRequest request, ISender sender, CancellationToken ct)
    {
        var user = await sender.Send(new CreateUserCommand(request.Email, request.Password, request.DisplayName, request.Role), ct);
        return TypedResults.Created($"/api/admin/users/{user.Id}", user);
    }

    private static async Task<NoContent> ChangeRole(Guid id, RoleRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new ChangeUserRoleCommand(id, request.Role), ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetActive(Guid id, ActiveRequest request, ISender sender, CancellationToken ct)
    {
        await sender.Send(new SetUserActiveCommand(id, request.IsActive), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<PagedResult<AuditEntryDto>>> AuditLogs(
        ISender sender,
        CancellationToken ct,
        Guid? projectId = null,
        Guid? actorId = null,
        string? action = null,
        Guid? resourceId = null,
        bool? succeeded = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50) =>
        TypedResults.Ok(await sender.Send(new GetAuditLogsQuery(projectId, actorId, action, resourceId, succeeded, from, to, page, pageSize), ct));
}
