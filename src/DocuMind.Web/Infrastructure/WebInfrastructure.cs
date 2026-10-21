using System.Net;
using System.Security.Claims;
using DocuMind.Web.Api;
using DocuMind.Web.Auth;
using DocuMind.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace DocuMind.Web.Infrastructure;

/// <summary>
/// Turns API failures into something a person can act on:
/// <list type="bullet">
/// <item>401 → the API session is gone: sign out here too and go to the login page.</item>
/// <item>Form posts → show the API's message at the top of the page we came from.</item>
/// <item>Pages → a friendly "not found" / "no access" / error page with the right status code.</item>
/// </list>
/// </summary>
public sealed class ApiExceptionFilter(ITempDataDictionaryFactory tempDataFactory, ILogger<ApiExceptionFilter> logger) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        var http = context.HttpContext;
        var (status, message) = context.Exception switch
        {
            ApiException api => (api.Status, api.UserMessage),
            HttpRequestException => (HttpStatusCode.BadGateway, "The DocuMind API isn't reachable right now. Check that it is running and try again."),
            _ => ((HttpStatusCode)0, ""),
        };
        if (status == 0) return; // not ours: let the normal error handling deal with it

        if (context.Exception is HttpRequestException) logger.LogError(context.Exception, "API unreachable");

        if (status == HttpStatusCode.Unauthorized)
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Result = new RedirectResult($"/account/login?returnUrl={Uri.EscapeDataString(http.Request.Path + http.Request.QueryString)}");
        }
        else if (!HttpMethods.IsGet(http.Request.Method))
        {
            tempDataFactory.GetTempData(http)[Flash.Error] = message;
            context.Result = new LocalRedirectResult(Urls.ReturnUrlFrom(http));
        }
        else
        {
            context.Result = new ViewResult
            {
                ViewName = "Problem",
                StatusCode = (int)status,
                ViewData = new ViewDataDictionary<ProblemViewModel>(
                    new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), context.ModelState)
                {
                    Model = new ProblemViewModel(status, message),
                },
            };
        }

        context.ExceptionHandled = true;
    }
}

public sealed record ProblemViewModel(HttpStatusCode Status, string Message);

/// <summary>Baseline browser hardening for every response.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var h = context.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "SAMEORIGIN"; // our own document viewer frames /documents/{id}/view
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return next(context);
    }
}

public static class Flash
{
    public const string Success = "flash-success";
    public const string Error = "flash-error";
}

public static class Urls
{
    /// <summary>Only same-site relative paths, so ?returnUrl= can't be used as an open redirect.</summary>
    public static string Safe(string? url, string fallback = "/") =>
        url is { Length: > 0 } u && u.StartsWith('/') && !u.StartsWith("//") && !u.StartsWith("/\\") ? u : fallback;

    /// <summary>Where a form post should go back to: its returnUrl field, else the page it was posted from.</summary>
    public static string ReturnUrlFrom(HttpContext http)
    {
        if (http.Request.HasFormContentType && http.Request.Form.TryGetValue("returnUrl", out var fromForm))
            return Safe(fromForm.ToString());

        if (Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer) &&
            referer.Host == http.Request.Host.Host && referer.Port == (http.Request.Host.Port ?? referer.Port))
            return Safe(referer.PathAndQuery);

        return "/";
    }
}

/// <summary>Common helpers for every controller.</summary>
public abstract class AppController : Controller
{
    protected Guid MyId => Guid.TryParse(User.FindFirstValue(DocuMindClaims.UserId), out var id) ? id : Guid.Empty;
    protected bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));
    protected bool IsManagerOrAbove => IsAdmin || User.IsInRole(nameof(UserRole.Manager));

    protected void Success(string message) => TempData[Flash.Success] = message;

    protected void Error(string message) => TempData[Flash.Error] = message;

    protected IActionResult Back(string? returnUrl, string fallback = "/") => LocalRedirect(Urls.Safe(returnUrl, fallback));

    protected ProjectAccess AccessFor(ProjectDto project) => new(project.MyRole, IsAdmin);
}

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string When(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("g") : "—";

    public static string StatusBadge(DocumentStatus status) => status switch
    {
        DocumentStatus.Completed => "text-bg-success",
        DocumentStatus.Failed => "text-bg-danger",
        DocumentStatus.Processing => "text-bg-info",
        _ => "text-bg-secondary",
    };

    public static string RoleBadge(ProjectRole? role) => role switch
    {
        ProjectRole.Owner => "text-bg-primary",
        ProjectRole.Editor => "text-bg-success",
        ProjectRole.Viewer => "text-bg-secondary",
        _ => "text-bg-dark",
    };

    public static string FileIcon(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "bi-file-earmark-pdf text-danger",
        ".docx" => "bi-file-earmark-word text-primary",
        ".md" => "bi-file-earmark-richtext",
        _ => "bi-file-earmark-text",
    };

    public static bool CanViewInline(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".pdf" or ".txt" or ".md";
}
