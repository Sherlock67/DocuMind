using System.Text.Json;
using System.Text.Json.Serialization;
using DocuMind.Web.Api;
using DocuMind.Web.Auth;
using DocuMind.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// ----- API access ----------------------------------------------------------------------------

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
json.Converters.Add(new JsonStringEnumConverter());
builder.Services.AddSingleton(json);

var apiBase = new Uri(builder.Configuration["Api:BaseUrl"] ?? throw new InvalidOperationException("Api:BaseUrl is not configured."));
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("documind-auth", c => c.BaseAddress = apiBase);
builder.Services.AddTransient<ApiAuthHandler>();
builder.Services.AddHttpClient<DocuMindApiClient>(c =>
    {
        c.BaseAddress = apiBase;
        c.Timeout = TimeSpan.FromMinutes(5); // batch uploads of large files
    })
    .AddHttpMessageHandler<ApiAuthHandler>();
builder.Services.AddSingleton<ApiSession>();
builder.Services.AddScoped<ApiCookieEvents>();

// ----- Sign-in: our own cookie, carrying the API tokens server-side --------------------------

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/account/login";
        o.LogoutPath = "/account/logout";
        o.AccessDeniedPath = "/account/access-denied";
        o.Cookie.Name = "DocuMind.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = false; // lifetime follows the API refresh token (set on sign-in)
        o.EventsType = typeof(ApiCookieEvents);
    });

// Secure by default: every page needs a signed-in user unless it says [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()); // every POST needs the form's antiforgery token
    o.Filters.Add<ApiExceptionFilter>();
}).AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter())); // JSON for the page scripts uses names, like the API

// Batch uploads: up to 20 files, 200 MB in total (the API enforces the same limits).
const long maxUpload = 210L * 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUpload);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/home/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapControllers();

app.Run();
