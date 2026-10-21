namespace DocuMind.Infrastructure.Persistence;

/// <summary>
/// Table names, shared by the EF Core mappings and the hand-written T-SQL (search, audit insert),
/// so the two can never drift apart.
/// </summary>
internal static class Tables
{
    public const string Projects = "Projects";
    public const string ProjectMembers = "ProjectMembers";
    public const string Folders = "Folders";
    public const string Collections = "Collections";
    public const string CollectionDocuments = "CollectionDocuments";
    public const string CollectionShares = "CollectionShares";
    public const string Documents = "Documents";
    public const string DocumentPages = "DocumentPages";
    public const string DocumentChunks = "DocumentChunks";
    public const string DocumentChunkTerms = "DocumentChunkTerms";
    public const string ChatSessions = "ChatSessions";
    public const string ChatMessages = "ChatMessages";
    public const string AuditLog = "AuditLog";

    public const string Users = "Users";
    public const string UserClaims = "UserClaims";
    public const string UserLogins = "UserLogins";
    public const string UserTokens = "UserTokens";
    public const string UserSessions = "UserSessions";
}
