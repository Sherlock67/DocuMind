using DocuMind.Application.Abstractions.Search;

namespace DocuMind.Application.Common.Auditing;

/// <summary>
/// Records which documents' content left the system through search results or an AI answer.
/// One entry per project touched, so each project Owner sees exactly what was read from their project.
/// </summary>
internal static class RetrievalAudit
{
    private const int MaxQueryLength = 200;

    public static void Record(IAuditTrail audit, IReadOnlyList<ChunkHit> hits, string via, string query, Guid? chatSessionId = null)
    {
        foreach (var project in hits.GroupBy(h => h.ProjectId))
        {
            var details = new Dictionary<string, string>
            {
                ["via"] = via,
                ["documentIds"] = string.Join(",", project.Select(h => h.DocumentId).Distinct()),
                ["chunks"] = project.Count().ToString(),
                ["query"] = query.Length <= MaxQueryLength ? query : query[..MaxQueryLength] + "…",
            };
            if (chatSessionId is { } id) details["chatSessionId"] = id.ToString();

            audit.Record(AuditActions.DocumentsRetrieved, "documents", null, project.Key == Guid.Empty ? null : project.Key, details);
        }
    }
}
