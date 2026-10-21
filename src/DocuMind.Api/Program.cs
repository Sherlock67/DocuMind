using System.Text.Json.Serialization;
using DocuMind.Api.Endpoints;
using DocuMind.Api.Infrastructure;
using DocuMind.Application;
using DocuMind.Application.Common.Options;
using DocuMind.Infrastructure;
using DocuMind.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiSecurity();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<BearerSecurityTransformer>());
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("sqlserver");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Size request limits for a full batch upload (+1 MB of multipart overhead).
var documentOptions = builder.Configuration.GetSection(DocumentOptions.SectionName).Get<DocumentOptions>() ?? new DocumentOptions();
var maxRequest = documentOptions.MaxBatchBytes + 1024 * 1024;
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxRequest;
    o.ValueCountLimit = 64;
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxRequest);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference().AllowAnonymous(); // interactive API docs at /scalar
app.MapHealthChecks("/health").AllowAnonymous();

app.MapSecurityEndpoints();
app.MapOrganizationEndpoints();
app.MapDocumentEndpoints();
app.MapSearchEndpoints();
app.MapChatEndpoints();

app.MapGet("/", () => Results.Redirect("/scalar")).AllowAnonymous().ExcludeFromDescription();

app.Run();
