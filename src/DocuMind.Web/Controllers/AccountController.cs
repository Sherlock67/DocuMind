using DocuMind.Web.Api;
using DocuMind.Web.Auth;
using DocuMind.Web.Infrastructure;
using DocuMind.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocuMind.Web.Controllers;

[Route("account")]
public sealed class AccountController(ApiSession session, DocuMindApiClient api) : AppController
{
    [AllowAnonymous, HttpGet("login")]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true) return Back(returnUrl);
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        try
        {
            var (auth, tokens) = await session.LoginAsync(model.Email.Trim(), model.Password, HttpContext);
            await SignInAsync(auth.User, tokens, model.RememberMe);
            return Back(model.ReturnUrl);
        }
        catch (ApiException ex)
        {
            ModelState.AddModelError("", ex.UserMessage);
        }
        catch (HttpRequestException)
        {
            ModelState.AddModelError("", "The DocuMind API isn't reachable right now.");
        }

        model.Password = "";
        return View(model);
    }

    [AllowAnonymous, HttpGet("register")]
    public IActionResult Register(string? returnUrl) => View(new RegisterViewModel { ReturnUrl = returnUrl });

    [AllowAnonymous, HttpPost("register")]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        try
        {
            var user = await session.RegisterAsync(model.Email.Trim(), model.Password, model.DisplayName.Trim(), HttpContext);
            var (auth, tokens) = await session.LoginAsync(model.Email.Trim(), model.Password, HttpContext);
            await SignInAsync(auth.User, tokens, persistent: false);
            Success($"Welcome, {user.DisplayName}! Your account has the {user.Role} role.");
            return Back(model.ReturnUrl);
        }
        catch (ApiException ex)
        {
            foreach (var (field, messages) in ex.Errors)
                foreach (var message in messages)
                    ModelState.AddModelError(field is "Email" or "Password" or "DisplayName" ? field : "", message);
            if (ex.Errors.Count == 0) ModelState.AddModelError("", ex.UserMessage);
        }
        catch (HttpRequestException)
        {
            ModelState.AddModelError("", "The DocuMind API isn't reachable right now.");
        }

        return View(model);
    }

    [HttpPost("logout")]
    public Task<IActionResult> Logout() => SignOutAsync(everywhere: false);

    [HttpPost("logout-everywhere")]
    public Task<IActionResult> LogoutEverywhere() => SignOutAsync(everywhere: true);

    [HttpGet("")]
    public async Task<IActionResult> Index() => View(new AccountViewModel(await api.MeAsync(), new ChangePasswordViewModel()));

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([Bind(Prefix = "ChangePassword")] ChangePasswordViewModel model)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await api.ChangePasswordAsync(model.CurrentPassword, model.NewPassword);
                Success("Password changed. Your other sessions have been signed out.");
                return RedirectToAction(nameof(Index));
            }
            catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.BadRequest)
            {
                ModelState.AddModelError("", ex.UserMessage);
            }
        }

        return View(nameof(Index), new AccountViewModel(await api.MeAsync(), new ChangePasswordViewModel()));
    }

    [AllowAnonymous, HttpGet("access-denied")]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Problem", new ProblemViewModel(System.Net.HttpStatusCode.Forbidden, "Your role doesn't allow this page."));
    }

    private async Task SignInAsync(Models.UserDto user, ApiTokens tokens, bool persistent)
    {
        var properties = new AuthenticationProperties { IsPersistent = persistent, AllowRefresh = true };
        ApiSession.Store(properties, tokens);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, ApiSession.ToPrincipal(user), properties);
    }

    private async Task<IActionResult> SignOutAsync(bool everywhere)
    {
        if (await HttpContext.GetTokenAsync(TokenNames.Access) is { Length: > 0 } token)
            await session.LogoutAsync(token, everywhere, HttpContext);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData[Flash.Success] = everywhere ? "You've been signed out on every device." : "You've been signed out.";
        return Redirect("/account/login");
    }
}
