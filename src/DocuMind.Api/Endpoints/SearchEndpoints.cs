using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Features.Search;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DocuMind.Api.Endpoints;

internal static class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").WithTags("Search");

        group.MapPost("/", Search)
            .WithSummary("Hybrid semantic + keyword search (no LLM call), optionally scoped to a project, folder tree, collection or documents.");

        return group;
    }

    private static async Task<Ok<IReadOnlyList<SearchHitDto>>> Search(SearchDocumentsQuery query, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(query, ct));
}
