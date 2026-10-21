using System.Runtime.CompilerServices;
using System.Text;
using DocuMind.Application.Abstractions.AI;
using DocuMind.Application.Abstractions.Data;
using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Auditing;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Options;
using DocuMind.Application.Common.Security;
using DocuMind.Application.Features.Chat.Commands;
using DocuMind.Application.Features.Search;
using DocuMind.Domain.Chat;
using DocuMind.Domain.Security;
using Microsoft.Extensions.Options;

namespace DocuMind.Application.Features.Chat;

internal sealed record PreparedAnswer(
    ChatSession Session,
    string Question,
    IReadOnlyList<Citation> Sources,
    ChatCompletionRequest? Request);

/// <summary>
/// The RAG pipeline shared by the blocking and streaming ask commands:
/// <see cref="PrepareAsync"/> (eager: session lookup, retrieval, prompt) then
/// <see cref="StreamAsync"/> (lazy: generation, then persistence of the exchange).
/// </summary>
internal sealed class RagChatService(
    IChatSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IHybridRetriever retriever,
    IChatCompletionService llm,
    IAccessControl access,
    ICurrentUser user,
    IAuditTrail audit,
    TimeProvider clock,
    IOptions<RagOptions> options)
{
    public async Task<PreparedAnswer> PrepareAsync(IQuestionRequest request, CancellationToken ct)
    {
        var (sessionId, question) = (request.SessionId, request.Question);
        var session = await sessions.GetByIdAsync(sessionId, ct);
        if (session is null || session.OwnerId != user.Id)
            throw new NotFoundException(nameof(ChatSession), sessionId);

        // Re-checked on every question: removing someone from a project also cuts off their existing chats.
        if (session.ProjectId is { } projectId) await access.RequireProjectAsync(projectId, ProjectRole.Viewer, ct);
        if (request.FolderId is { } folderId) await access.RequireFolderAsync(folderId, ProjectRole.Viewer, ct);
        if (request.CollectionId is { } collectionId) await access.RequireCollectionAsync(collectionId, CollectionPermission.View, ct);

        // A project-bound conversation never retrieves outside its project, whatever the request narrows to,
        // and nothing the caller can't read is ever put in front of the model.
        var scope = new SearchScope(session.ProjectId, request.FolderId, request.CollectionId, request.DocumentIds);
        var hits = await retriever.RetrieveAsync(question, options.Value.ContextChunks, scope, access.Principal, ct);
        RetrievalAudit.Record(audit, hits, "chat", question, session.Id);
        if (hits.Count == 0)
            return new PreparedAnswer(session, question, [], Request: null);

        var history = await sessions.GetRecentMessagesAsync(sessionId, options.Value.HistoryMessages, ct);
        return new PreparedAnswer(session, question, RagPrompt.ToCitations(hits), RagPrompt.Build(question, hits, history));
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(PreparedAnswer prepared, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SourcesEvent(prepared.Sources.Select(CitationDto.From).ToList());

        var answer = new StringBuilder();
        if (prepared.Request is null)
        {
            answer.Append(RagPrompt.NoSourcesAnswer);
            yield return new DeltaEvent(RagPrompt.NoSourcesAnswer);
        }
        else
        {
            await foreach (var delta in llm.StreamAsync(prepared.Request, ct))
            {
                answer.Append(delta);
                yield return new DeltaEvent(delta);
            }
        }

        // Only reached when generation completed; a client disconnect cancels before this,
        // so half-written answers are never stored.
        var text = answer.ToString();
        var cited = RagPrompt.ExtractCitedSourceNumbers(text, prepared.Sources.Count);
        var citedSources = prepared.Sources.Where(s => cited.Contains(s.SourceNumber)).ToList();

        var reply = prepared.Session.RecordExchange(prepared.Question, text, citedSources, clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(ct);

        yield return new CompletedEvent(reply.Id, cited);
    }
}
