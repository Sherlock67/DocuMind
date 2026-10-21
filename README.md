# DocuMind

AI-powered document search & RAG assistant — .NET 10, Clean Architecture, CQRS.

Upload PDFs, Word files, text and Markdown (one at a time or in batches); organize them into projects,
folders and collections; preview, download, rename and move them; search them by meaning and keyword;
and chat with an assistant (Claude) that answers only from your documents with inline citations.
Accounts, role-based access (Admin / Manager / User), project and collection sharing, and a full audit
trail ensure people only ever retrieve documents they are allowed to see.

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for the full system design.

## Stack

| Concern | Choice |
|---|---|
| API | ASP.NET Core 10 Minimal APIs, SSE streaming, OpenAPI + Scalar UI |
| CQRS | In-house mediator with logging + FluentValidation pipeline behaviors |
| Data | **SQL Server 2025** (LocalDB for development): native `VECTOR` + `JSON`, BM25 keyword index, EF Core 10 |
| LLM | Claude (`claude-opus-5-5`) via the official `Anthropic` SDK |
| Embeddings | Voyage AI `voyage-3.5` (default) or local Ollama `mxbai-embed-large` — both 1024-d |
| Extraction | PdfPig (PDF), DocumentFormat.OpenXml (DOCX) |
| Auth | ASP.NET Core Identity, JWT access tokens + rotating refresh tokens, rate limiting |
| Web UI | ASP.NET Core MVC with Razor views and Bootstrap 5 — a separate app that calls the API |
| Tests | xUnit + NSubstitute |

## Project layout

```
src/
  DocuMind.Domain          Aggregates: Project, Folder, Collection, Document(+Chunks, Pages), ChatSession
  DocuMind.Application     Commands / Queries / Handlers / Validators, ports (interfaces)
    Features/Auth          Register, login, refresh, logout, change password
    Features/Users         Admin: users and roles
    Features/Audit         Audit-log queries
    Features/Projects      Projects, members (sharing) and document counts by status
    Features/Folders       Nested folder tree: create, rename, move, delete
    Features/Collections   Private/project collections and collection sharing
    Features/Documents     Batch upload, ingest, rename, move, preview, download, status, delete
    Features/Search        HybridRetriever, ReciprocalRankFusion, SearchDocuments
    Features/Chat          RagChatService, RagPrompt, AskQuestion, StreamAnswer
  DocuMind.Infrastructure  EF Core (write + read contexts), hybrid-search T-SQL, Claude, Voyage/Ollama,
                           extractors, chunker, file storage, background ingestion worker
  DocuMind.Api             Endpoints, exception → ProblemDetails mapping, composition root
  DocuMind.Web             ASP.NET Core MVC web app (controllers + Razor views) that calls the API
    Controllers            Account, Projects (+members), Folders, Collections (+sharing), Documents, Admin, Audit
    Views                  Razor .cshtml pages (Bootstrap 5); modals are plain forms posting to controllers
    Auth/ApiSession        Sign-in cookie that keeps the API tokens server-side and refreshes them
    Api/DocuMindApiClient  Typed HttpClient over the REST API
tests/
  DocuMind.UnitTests
  DocuMind.IntegrationTests  Against a real SQL Server 2025 (LocalDB)
```

## Run locally

1. Database: **SQL Server 2025 LocalDB** (installed with Visual Studio, or via the SQL Server 2025 Express
   installer → *LocalDB*). Nothing to configure — the default connection string points at
   `(localdb)\MSSQLLocalDB` and the database is created on first run. Any SQL Server 2025 or Azure SQL
   works too: set `ConnectionStrings:SqlServer`. Older versions (2022 and earlier) lack the `VECTOR` type.
2. Set API keys (user-secrets keeps them out of the repo):
   ```bash
   dotnet user-secrets --project src/DocuMind.Api set "Anthropic:ApiKey" "<your-anthropic-key>"
   ```
   ```bash
   dotnet user-secrets --project src/DocuMind.Api set "Embeddings:ApiKey" "<your-voyage-key>"
   ```
   (`ANTHROPIC_API_KEY` / `VOYAGE_API_KEY` environment variables also work.)
   For a stable JWT signing key across restarts (required outside Development):
   ```bash
   dotnet user-secrets --project src/DocuMind.Api set "Auth:SigningKey" "<at least 32 random characters>"
   ```
   To embed locally instead: `ollama pull mxbai-embed-large`, then set `Embeddings:Provider=Ollama`
   and `Embeddings:Model=mxbai-embed-large`.
3. Run the API (Development applies migrations on startup) — it listens on http://localhost:5288:
   ```bash
   dotnet run --project src/DocuMind.Api
   ```
4. In a second terminal, run the web app — it listens on http://localhost:5157 and calls the API
   at `Api:BaseUrl` in `src/DocuMind.Web/appsettings.json`:
   ```bash
   dotnet run --project src/DocuMind.Web
   ```
5. Open **http://localhost:5157** and create an account — the first account becomes Admin.
   (In Visual Studio: *Configure Startup Projects → Multiple startup projects* → DocuMind.Api and DocuMind.Web.)
   The API's interactive docs are at http://localhost:5288/scalar.

Set `Auth:SigningKey` (step 2) even in development if you restart the API often: without it the API makes
a throwaway key per run, so restarting it signs everyone out of the web app.

## Try it

Register (the first account becomes Admin) and log in; send the returned `accessToken` as a bearer token
on every other call (`TOKEN` below). In Scalar, use the **Authorize** button.

```bash
curl -X POST http://localhost:5288/api/auth/register -H "Content-Type: application/json" -d "{\"email\":\"admin@example.com\",\"password\":\"<a strong password>\",\"displayName\":\"Admin\"}"
```
```bash
curl -X POST http://localhost:5288/api/auth/login -H "Content-Type: application/json" -d "{\"email\":\"admin@example.com\",\"password\":\"<a strong password>\"}"
```

Create a project, then upload several files into it in one request:

```bash
curl -X POST http://localhost:5288/api/projects -H "Authorization: Bearer TOKEN" -H "Content-Type: application/json" -d "{\"name\":\"HR policies\"}"
```
```bash
curl -H "Authorization: Bearer TOKEN" -F "projectId=<projectId>" -F "files=@handbook.pdf" -F "files=@benefits.docx" -F "files=@faq.md" http://localhost:5288/api/documents
```
```bash
curl -H "Authorization: Bearer TOKEN" "http://localhost:5288/api/documents/status?ids=<id1>&ids=<id2>&ids=<id3>"
```

Share the project with a colleague (they must have registered):

```bash
curl -X POST http://localhost:5288/api/projects/<projectId>/members -H "Authorization: Bearer TOKEN" -H "Content-Type: application/json" -d "{\"email\":\"colleague@example.com\",\"role\":\"Viewer\"}"
```

Start a conversation bound to the project and ask a question:

```bash
curl -X POST http://localhost:5288/api/chat/sessions -H "Authorization: Bearer TOKEN" -H "Content-Type: application/json" -d "{\"projectId\":\"<projectId>\"}"
```
```bash
curl -N -X POST http://localhost:5288/api/chat/sessions/<sessionId>/messages/stream -H "Authorization: Bearer TOKEN" -H "Content-Type: application/json" -d "{\"question\":\"What is the parental leave policy?\"}"
```

The stream emits `sources` (passages given to the model), then `delta` events (answer tokens),
then `done` (`messageId` and the source numbers actually cited).

## Development

```bash
dotnet test
```

`tests/DocuMind.IntegrationTests` runs against a throw-away database on LocalDB (or the server in
`DOCUMIND_TEST_SQLSERVER`) and covers the hand-written T-SQL: access-filtered vector and BM25 search,
collection shares, folder scopes, cascades, JSON columns and the audit writer. It skips itself when no
SQL Server 2025 is reachable.
```bash
dotnet ef migrations add <Name> --context AppDbContext --project src/DocuMind.Infrastructure --startup-project src/DocuMind.Api --output-dir Persistence/Migrations
```

## Configuration (`appsettings.json`)

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:SqlServer` / `SqlServerRead` | `(localdb)\MSSQLLocalDB` | Read side falls back to primary when empty |
| `Anthropic:Model` / `Effort` / `MaxTokens` | `claude-opus-5-5` / `medium` / 16000 | |
| `Embeddings:Provider` / `Model` / `Dimensions` | `Voyage` / `voyage-3.5` / 1024 | Dimensions must match the `vector(N)` column |
| `Rag:ContextChunks` / `CandidatePoolSize` | 8 / 30 | Passages sent to the LLM / fetched per retriever |
| `Rag:ChunkSize` / `ChunkOverlap` | 1200 / 200 | Characters |
| `Documents:MaxFileBytes` / `MaxFilesPerBatch` / `MaxBatchBytes` | 50 MB / 20 / 200 MB | Per file / per upload request |
| `Ingestion:MaxConcurrency` | 2 | Documents ingested in parallel |
| `Auth:SigningKey` | (empty) | ≥ 32 bytes; from user-secrets / `Auth__SigningKey` only |
| `Auth:AccessTokenMinutes` / `RefreshTokenDays` | 15 / 14 | |
| `Auth:AllowSelfRegistration` | true | When false, only Admins create accounts (first account is always allowed) |
