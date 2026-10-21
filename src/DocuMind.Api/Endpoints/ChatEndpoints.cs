using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Common.Models;
using DocuMind.Application.Features.Chat;
using DocuMind.Application.Features.Chat.Commands;
using DocuMind.Application.Features.Chat.Queries;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DocuMind.Api.Endpoints;

internal static class ChatEndpoints
{
    public sealed record CreateSessionRequest(Guid? ProjectId, string? Title);

    /// <summary>Optional filters narrow retrieval further within the session's project.</summary>
    public sealed record AskRequest(string Question, Guid? FolderId, Guid? CollectionId, IReadOnlyList<Guid>? DocumentIds);

    public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat/sessions").WithTags("Chat");

        group.MapPost("/", Create).WithSummary("Start a conversation, optionally bound to a project.");
        group.MapGet("/", List).WithSummary("List conversations (optionally for one project), most recently active first.");
        group.MapGet("/{id:guid}", Get).WithName("GetChatSession").WithSummary("Get a conversation with its messages and citations.");

        group.MapPost("/{id:guid}/messages", Ask)
            .WithSummary("Ask a question and wait for the complete cited answer.");

        group.MapPost("/{id:guid}/messages/stream", Stream)
            .WithSummary("Ask a question and stream the answer as Server-Sent Events: sources, delta*, done.");

        return group;
    }

    private static async Task<CreatedAtRoute<ChatSessionSummaryDto>> Create(CreateSessionRequest? request, ISender sender, CancellationToken ct)
    {
        var session = await sender.Send(new CreateChatSessionCommand(request?.ProjectId, request?.Title), ct);
        return TypedResults.CreatedAtRoute(session, "GetChatSession", new { id = session.Id });
    }

    private static async Task<Ok<PagedResult<ChatSessionSummaryDto>>> List(ISender sender, CancellationToken ct, Guid? projectId = null, int page = 1, int pageSize = 20) =>
        TypedResults.Ok(await sender.Send(new GetChatSessionsQuery(projectId, page, pageSize), ct));

    private static async Task<Ok<ChatSessionDto>> Get(Guid id, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new GetChatSessionQuery(id), ct));

    private static async Task<Ok<AnswerDto>> Ask(Guid id, AskRequest request, ISender sender, CancellationToken ct) =>
        TypedResults.Ok(await sender.Send(new AskQuestionCommand(id, request.Question, request.FolderId, request.CollectionId, request.DocumentIds), ct));

    /// <summary>
    /// Validation, the session lookup and retrieval happen inside Send, before any bytes are written,
    /// so a bad request still gets a normal 400/404 ProblemDetails instead of a broken event stream.
    /// </summary>
    private static async Task<ServerSentEventsResult<object>> Stream(Guid id, AskRequest request, ISender sender, CancellationToken ct)
    {
        var events = await sender.Send(new StreamAnswerCommand(id, request.Question, request.FolderId, request.CollectionId, request.DocumentIds), ct);
        return TypedResults.ServerSentEvents(ToSse(events, ct));
    }

    private static async IAsyncEnumerable<SseItem<object>> ToSse(IAsyncEnumerable<ChatStreamEvent> events, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in events.WithCancellation(ct))
            yield return new SseItem<object>(evt, evt.Type); // object => serialised with its runtime type
    }
}
