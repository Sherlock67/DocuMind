using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace DocuMind.Api.Infrastructure;

/// <summary>Declares JWT bearer auth in the OpenAPI document so Scalar can send the token.</summary>
internal sealed class BearerSecurityTransformer : IOpenApiDocumentTransformer
{
    private const string Scheme = "Bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Access token from POST /api/auth/login.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(Scheme, document)] = [] });
        return Task.CompletedTask;
    }
}
