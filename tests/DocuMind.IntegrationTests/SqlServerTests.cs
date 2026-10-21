using DocuMind.Application.Abstractions.Search;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Domain.Auditing;
using DocuMind.Domain.Chat;
using DocuMind.Domain.Documents;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;
using DocuMind.Infrastructure.Identity;
using DocuMind.Infrastructure.Persistence;
using DocuMind.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;

namespace DocuMind.IntegrationTests;

/// <summary>Seeds users, projects and processed documents straight through the write model.</summary>
public abstract class SqlServerTestBase(SqlServerFixture fx)
{
    protected static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    protected readonly SqlServerFixture Fx = fx;
    private readonly IKeywordTokenizer _tokenizer = new KeywordTokenizer();

    /// <summary>A 1024-d vector with 1 in the given dimensions (orthogonal "topics" for predictable similarity).</summary>
    protected static float[] Vec(params int[] hot)
    {
        var v = new float[1024];
        foreach (var i in hot) v[i] = 1;
        return v;
    }

    protected static async Task<Guid> AddUserAsync(AppDbContext db, UserRole role = UserRole.User)
    {
        var email = $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Test",
            Role = role,
            CreatedAt = Now,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    protected Document ProcessedDocument(Guid projectId, Guid uploader, Guid? folderId, params (string Text, float[] Vector)[] chunks)
    {
        var doc = Document.Create(projectId, folderId, $"{Guid.NewGuid():N}.txt", "text/plain", 10, "path", Guid.NewGuid().ToString("N"), uploader, Now);
        doc.MarkProcessing(Now);
        doc.CompleteIngestion(
            chunks.Select((c, i) => DocumentChunk.Create(doc.Id, i, c.Text, null, c.Vector, _tokenizer.TermFrequencies(c.Text))),
            [DocumentPage.Create(doc.Id, 0, null, string.Join("\n", chunks.Select(c => c.Text)))],
            pageCount: 0,
            Now);
        return doc;
    }

    protected IChunkSearchService Search() => new SqlChunkSearchService(Fx.Connections, new KeywordTokenizer(), Fx.Embedding);
}

[Collection(SqlServerCollection.Name)]
public sealed class RetrievalAccessTests(SqlServerFixture fx) : SqlServerTestBase(fx)
{
    /// <summary>Two projects; the reader belongs only to A. B's content is the better match for every query.</summary>
    private async Task<(Guid Reader, Project A, Project B, Document DocA, Document DocB)> TwoProjectsAsync()
    {
        await using var db = Fx.CreateWriteContext();
        var owner = await AddUserAsync(db, UserRole.Manager);
        var reader = await AddUserAsync(db);

        var a = Project.Create("A", null, owner, Now);
        a.AddMember(reader, ProjectRole.Viewer, owner, Now);
        var b = Project.Create("B", null, owner, Now);
        db.Projects.AddRange(a, b);

        var docA = ProcessedDocument(a.Id, owner, null, ("quarterly zebra report for project alpha", Vec(1)));
        var docB = ProcessedDocument(b.Id, owner, null, ("zebra zebra zebra confidential salaries", Vec(0)));
        db.Documents.AddRange(docA, docB);
        await db.SaveChangesAsync();
        return (reader, a, b, docA, docB);
    }

    [SkippableFact]
    public async Task Vector_search_never_returns_chunks_from_projects_the_user_cannot_read()
    {
        Fx.SkipIfUnavailable();
        var (reader, _, _, docA, _) = await TwoProjectsAsync();

        var hits = await Search().VectorSearchAsync(Vec(0), 50, SearchScope.Everything, new AccessPrincipal(reader, false), default);

        Assert.Equal([docA.Id], hits.Select(h => h.DocumentId).Distinct());
    }

    [SkippableFact]
    public async Task Keyword_search_never_returns_chunks_from_projects_the_user_cannot_read()
    {
        Fx.SkipIfUnavailable();
        var (reader, _, _, docA, _) = await TwoProjectsAsync();

        var hits = await Search().KeywordSearchAsync("zebra salaries", 50, SearchScope.Everything, new AccessPrincipal(reader, false), default);

        var hit = Assert.Single(hits);
        Assert.Equal(docA.Id, hit.DocumentId);
        Assert.True(hit.Score > 0);
    }

    [SkippableFact]
    public async Task Admins_retrieve_across_all_projects()
    {
        Fx.SkipIfUnavailable();
        var (_, _, _, docA, docB) = await TwoProjectsAsync();
        await using var db = Fx.CreateWriteContext();
        var admin = await AddUserAsync(db, UserRole.Admin);

        var hits = await Search().VectorSearchAsync(Vec(0), 1000, SearchScope.Everything, new AccessPrincipal(admin, true), default);

        Assert.Contains(hits, h => h.DocumentId == docA.Id);
        Assert.Contains(hits, h => h.DocumentId == docB.Id);
    }

    [SkippableFact]
    public async Task A_shared_collection_makes_exactly_its_documents_retrievable()
    {
        Fx.SkipIfUnavailable();
        var (reader, _, b, docA, docB) = await TwoProjectsAsync();

        await using (var db = Fx.CreateWriteContext())
        {
            var hidden = ProcessedDocument(b.Id, b.CreatedBy, null, ("zebra board minutes", Vec(0)));
            db.Documents.Add(hidden);

            var collection = Collection.Create(b.Id, b.CreatedBy, "Shared pack", null, CollectionVisibility.Private, Now);
            collection.AddDocuments([docB.Id], Now);
            collection.ShareWith(reader, b.CreatedBy, Now);
            db.Collections.Add(collection);
            await db.SaveChangesAsync();
        }

        var principal = new AccessPrincipal(reader, false);
        var vector = await Search().VectorSearchAsync(Vec(0), 50, SearchScope.Everything, principal, default);
        var keyword = await Search().KeywordSearchAsync("zebra", 50, SearchScope.Everything, principal, default);

        Assert.Equal(new[] { docA.Id, docB.Id }.Order(), vector.Select(h => h.DocumentId).Distinct().Order());
        Assert.Equal(new[] { docA.Id, docB.Id }.Order(), keyword.Select(h => h.DocumentId).Distinct().Order());
        Assert.Equal(docB.Id, vector[0].DocumentId); // exact vector match ranks first
    }

    [SkippableFact]
    public async Task Project_and_document_scopes_narrow_results_within_what_is_readable()
    {
        Fx.SkipIfUnavailable();
        var (reader, a, b, docA, _) = await TwoProjectsAsync();
        var principal = new AccessPrincipal(reader, false);

        Assert.NotEmpty(await Search().VectorSearchAsync(Vec(1), 10, new SearchScope(ProjectId: a.Id), principal, default));
        Assert.Empty(await Search().VectorSearchAsync(Vec(1), 10, new SearchScope(ProjectId: b.Id), principal, default));
        Assert.Empty(await Search().VectorSearchAsync(Vec(1), 10, new SearchScope(DocumentIds: [Guid.NewGuid()]), principal, default));
        Assert.Single(await Search().VectorSearchAsync(Vec(1), 10, new SearchScope(DocumentIds: [docA.Id]), principal, default));
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class RetrievalBehaviourTests(SqlServerFixture fx) : SqlServerTestBase(fx)
{
    [SkippableFact]
    public async Task Folder_scope_includes_subfolders_through_the_recursive_cte()
    {
        Fx.SkipIfUnavailable();
        await using var db = Fx.CreateWriteContext();
        var owner = await AddUserAsync(db, UserRole.Manager);
        var project = Project.Create("Folders", null, owner, Now);
        var root = Folder.Create(project.Id, null, "Root", Now);
        var child = Folder.Create(project.Id, root, "Child", Now);
        var other = Folder.Create(project.Id, null, "Other", Now);
        db.Projects.Add(project);
        db.Folders.AddRange(root, child, other);
        var nested = ProcessedDocument(project.Id, owner, child.Id, ("nested policy text", Vec(5)));
        db.Documents.Add(nested);
        await db.SaveChangesAsync();

        var principal = new AccessPrincipal(owner, false);
        var inRoot = await Search().VectorSearchAsync(Vec(5), 10, new SearchScope(FolderId: root.Id), principal, default);
        var inOther = await Search().VectorSearchAsync(Vec(5), 10, new SearchScope(FolderId: other.Id), principal, default);

        Assert.Equal(nested.Id, Assert.Single(inRoot).DocumentId);
        Assert.Empty(inOther);
    }

    [SkippableFact]
    public async Task Bm25_ranks_higher_term_frequency_first_and_ignores_stop_word_only_queries()
    {
        Fx.SkipIfUnavailable();
        await using var db = Fx.CreateWriteContext();
        var owner = await AddUserAsync(db, UserRole.Manager);
        var project = Project.Create("Ranking", null, owner, Now);
        db.Projects.Add(project);
        var doc = ProcessedDocument(project.Id, owner, null,
            ("kumquat orchard notes", Vec(7)),
            ("kumquat kumquat kumquat harvest", Vec(8)),
            ("unrelated text entirely", Vec(9)));
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var principal = new AccessPrincipal(owner, false);
        var hits = await Search().KeywordSearchAsync("Kumquat", 10, new SearchScope(ProjectId: project.Id), principal, default);

        Assert.Equal([1, 0], hits.Select(h => h.ChunkIndex));
        Assert.Empty(await Search().KeywordSearchAsync("the and of", 10, SearchScope.Everything, principal, default));
    }

    [SkippableFact]
    public async Task Reprocessing_replaces_chunks_and_their_keyword_terms()
    {
        Fx.SkipIfUnavailable();
        Guid docId;
        await using (var db = Fx.CreateWriteContext())
        {
            var owner = await AddUserAsync(db, UserRole.Manager);
            var project = Project.Create("Reprocess", null, owner, Now);
            db.Projects.Add(project);
            var doc = ProcessedDocument(project.Id, owner, null, ("persimmon first version", Vec(3)));
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            docId = doc.Id;
        }

        await using (var db = Fx.CreateWriteContext())
        {
            var doc = await db.Documents.Include(d => d.Chunks).ThenInclude(c => c.Terms).Include(d => d.Pages).SingleAsync(d => d.Id == docId);
            doc.Requeue(Now);
            doc.MarkProcessing(Now);
            var tokenizer = new KeywordTokenizer() as IKeywordTokenizer;
            doc.CompleteIngestion(
                [DocumentChunk.Create(doc.Id, 0, "quince second version", null, Vec(4), tokenizer.TermFrequencies("quince second version"))],
                [DocumentPage.Create(doc.Id, 0, null, "quince")], 0, Now);
            await db.SaveChangesAsync();
        }

        await using var read = Fx.CreateWriteContext();
        var terms = await read.Set<ChunkTerm>().Where(t => read.Set<DocumentChunk>().Any(c => c.Id == t.ChunkId && c.DocumentId == docId)).Select(t => t.Term).ToListAsync();
        Assert.Equal(["quince", "second", "version"], terms.Order());
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class SchemaAndAuditTests(SqlServerFixture fx) : SqlServerTestBase(fx)
{
    [SkippableFact]
    public async Task Audit_entries_are_inserted_and_details_round_trip_as_json()
    {
        Fx.SkipIfUnavailable();
        var resourceId = Guid.NewGuid();
        var entries = Enumerable.Range(0, 160).Select(i => AuditEntry.Create(
            Now, Guid.NewGuid(), "a@b.test", "document.downloaded", true, "document", resourceId, Guid.NewGuid(),
            new Dictionary<string, string> { ["fileName"] = $"report-{i}.pdf", ["quote"] = "it's \"fine\"" }, "127.0.0.1", "tests")).ToList();

        await new SqlServerAuditLogWriter(Fx.Connections).WriteAsync(entries, default); // > one 150-row batch

        await using var read = Fx.CreateReadContext();
        var stored = await read.AuditEntries.Where(e => e.ResourceId == resourceId).ToListAsync();
        Assert.Equal(160, stored.Count);
        Assert.Contains(stored, e => e.Details["fileName"] == "report-0.pdf" && e.Details["quote"] == "it's \"fine\"");
    }

    [SkippableFact]
    public async Task Root_folder_names_are_unique_per_project()
    {
        Fx.SkipIfUnavailable();
        await using var db = Fx.CreateWriteContext();
        var owner = await AddUserAsync(db, UserRole.Manager);
        var project = Project.Create("Unique", null, owner, Now);
        db.Projects.Add(project);
        db.Folders.AddRange(Folder.Create(project.Id, null, "Policies", Now), Folder.Create(project.Id, null, "Policies", Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task A_folder_subtree_can_be_deleted_in_one_save()
    {
        Fx.SkipIfUnavailable();
        Guid projectId;
        await using (var db = Fx.CreateWriteContext())
        {
            var owner = await AddUserAsync(db, UserRole.Manager);
            var project = Project.Create("Tree", null, owner, Now);
            var parent = Folder.Create(project.Id, null, "Parent", Now);
            var child = Folder.Create(project.Id, parent, "Child", Now);
            db.Projects.Add(project);
            db.Folders.AddRange(parent, child);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        await using (var db = Fx.CreateWriteContext())
        {
            db.Folders.RemoveRange(await db.Folders.Where(f => f.ProjectId == projectId).ToListAsync());
            await db.SaveChangesAsync();
        }

        await using var check = Fx.CreateWriteContext();
        Assert.False(await check.Folders.AnyAsync(f => f.ProjectId == projectId));
    }

    [SkippableFact]
    public async Task Deleting_a_project_cascades_to_folders_members_collections_and_chats()
    {
        Fx.SkipIfUnavailable();
        Guid projectId;
        await using (var db = Fx.CreateWriteContext())
        {
            var owner = await AddUserAsync(db, UserRole.Manager);
            var project = Project.Create("Cascade", null, owner, Now);
            var parent = Folder.Create(project.Id, null, "Parent", Now);
            db.Projects.Add(project);
            db.Folders.AddRange(parent, Folder.Create(project.Id, parent, "Child", Now));
            db.Collections.Add(Collection.Create(project.Id, owner, "C", null, CollectionVisibility.Project, Now));
            db.ChatSessions.Add(ChatSession.Start(owner, project.Id, "Chat", Now));
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        await using (var db = Fx.CreateWriteContext())
        {
            db.Projects.Remove(await db.Projects.SingleAsync(p => p.Id == projectId));
            await db.SaveChangesAsync();
        }

        await using var check = Fx.CreateReadContext();
        Assert.False(await check.Folders.AnyAsync(f => f.ProjectId == projectId));
        Assert.False(await check.ProjectMembers.AnyAsync(m => m.ProjectId == projectId));
        Assert.False(await check.Collections.AnyAsync(c => c.ProjectId == projectId));
        Assert.False(await check.ChatSessions.AnyAsync(s => s.ProjectId == projectId));
    }

    [SkippableFact]
    public async Task Chat_citations_round_trip_through_the_json_column_in_order()
    {
        Fx.SkipIfUnavailable();
        Guid sessionId;
        await using (var db = Fx.CreateWriteContext())
        {
            var owner = await AddUserAsync(db);
            var session = ChatSession.Start(owner, null, null, Now);
            session.RecordExchange("What is the leave policy?", "20 days [1].",
                [new Citation(1, Guid.NewGuid(), "handbook.pdf", Guid.NewGuid(), 4, "Employees get 20 days", 0.03)], Now);
            db.ChatSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        await using var read = Fx.CreateReadContext();
        var messages = await read.ChatMessages.Where(m => m.SessionId == sessionId).OrderBy(m => m.CreatedAt).ThenBy(m => m.Role).ToListAsync();

        Assert.Equal([ChatRole.User, ChatRole.Assistant], messages.Select(m => m.Role));
        var citation = Assert.Single(messages[1].Citations);
        Assert.Equal(("handbook.pdf", 4), (citation.FileName, citation.PageNumber));
    }
}
