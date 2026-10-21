using DocuMind.Web.Api;
using DocuMind.Web.Infrastructure;
using DocuMind.Web.Models;
using DocuMind.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocuMind.Web.Controllers;

[Authorize(Roles = nameof(UserRole.Admin))]
[Route("admin/users")]
public sealed class AdminController(DocuMindApiClient api) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Users(string? q, int page = 1) =>
        View(new UsersViewModel(await api.UsersAsync(Math.Max(1, page), 25, q), q, MyId));

    [HttpPost("")]
    public async Task<IActionResult> Create(string email, string password, string displayName, UserRole role)
    {
        await api.CreateUserAsync(email.Trim(), password, displayName.Trim(), role);
        Success($"Account created for {email.Trim()} ({role}).");
        return Redirect("/admin/users");
    }

    [HttpPost("{id:guid}/role")]
    public async Task<IActionResult> ChangeRole(Guid id, UserRole role, string? returnUrl)
    {
        await api.ChangeUserRoleAsync(id, role);
        Success($"Role changed to {role}. It applies to their open sessions within seconds.");
        return Back(returnUrl, "/admin/users");
    }

    [HttpPost("{id:guid}/active")]
    public async Task<IActionResult> SetActive(Guid id, bool isActive, string? returnUrl)
    {
        await api.SetUserActiveAsync(id, isActive);
        Success(isActive ? "Account reactivated." : "Account deactivated and signed out everywhere.");
        return Back(returnUrl, "/admin/users");
    }
}

/// <summary>Audit trail: everything for Admins, a single project's trail for its Owners.</summary>
public sealed class AuditController(DocuMindApiClient api) : AppController
{
    [HttpGet("/audit")]
    public async Task<IActionResult> Index([FromQuery] AuditFilter filter)
    {
        var project = filter.ProjectId is { } pid ? await api.ProjectAsync(pid) : null;
        var entries = await api.AuditAsync(new AuditQuery(
            filter.ProjectId,
            string.IsNullOrWhiteSpace(filter.Action) ? null : filter.Action.Trim(),
            filter.Succeeded,
            filter.From is { } from ? new DateTimeOffset(DateTime.SpecifyKind(from.Date, DateTimeKind.Local)) : null,
            filter.To is { } to ? new DateTimeOffset(DateTime.SpecifyKind(to.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local)) : null,
            Math.Max(1, filter.Page)));

        return View(new AuditViewModel(entries, filter, project));
    }
}

[Route("home")]
public sealed class HomeController : Controller
{
    [AllowAnonymous, HttpGet("error")]
    public IActionResult Error() => View();
}
