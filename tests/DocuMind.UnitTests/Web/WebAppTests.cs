using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocuMind.Web.Auth;
using DocuMind.Web.Infrastructure;
using DocuMind.Web.Models;
using DocuMind.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuMind.UnitTests.Web;

public class ApiSessionTests
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary>Fake API: answers /api/auth/refresh like the real one (JSON body + Set-Cookie) and counts calls.</summary>
    private sealed class FakeApi(HttpStatusCode status = HttpStatusCode.OK, int delayMs = 50) : HttpMessageHandler
    {
        public int RefreshCalls;
        public string? LastCookieHeader;
        public string? LastForwardedFor;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref RefreshCalls);
            LastCookieHeader = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(";", c) : null;
            LastForwardedFor = request.Headers.TryGetValues("X-Forwarded-For", out var f) ? f.Single() : null;
            await Task.Delay(delayMs, ct);

            if (status != HttpStatusCode.OK) return new HttpResponseMessage(status);

            var user = new UserDto(Guid.NewGuid(), "a@b.test", "Ada", UserRole.Manager, true, DateTimeOffset.UtcNow, null);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new AuthResponse("new-access", DateTimeOffset.UtcNow.AddMinutes(15), user), Json), Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("Set-Cookie", "documind_refresh=new-refresh; expires=Wed, 04 Nov 2026 10:00:00 GMT; path=/api/auth; samesite=strict; httponly");
            return response;
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://api.test/") };
    }

    private static (ApiSession Session, FakeApi Api) Create(HttpStatusCode status = HttpStatusCode.OK)
    {
        var api = new FakeApi(status);
        return (new ApiSession(new Factory(api), Json, new MemoryCache(new MemoryCacheOptions()), NullLogger<ApiSession>.Instance), api);
    }

    private static HttpContext Browser()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        return http;
    }

    [Fact]
    public async Task Refresh_sends_the_token_as_the_API_cookie_and_reads_the_rotated_one_from_Set_Cookie()
    {
        var (session, api) = Create();

        var result = await session.RefreshAsync("old-refresh", Browser());

        Assert.NotNull(result);
        Assert.Equal("documind_refresh=old-refresh", api.LastCookieHeader);
        Assert.Equal("203.0.113.7", api.LastForwardedFor); // the API rate-limits and audits the real user
        Assert.Equal("new-access", result.Value.Tokens.AccessToken);
        Assert.Equal("new-refresh", result.Value.Tokens.RefreshToken);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero), result.Value.Tokens.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task Concurrent_refreshes_of_the_same_token_call_the_API_once()
    {
        // Two parallel requests rotating the same token would make the API see a replayed
        // refresh token and revoke the whole session.
        var (session, api) = Create();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.RefreshAsync("same-token", Browser())));

        Assert.Equal(1, api.RefreshCalls);
        Assert.All(results, r => Assert.Equal("new-refresh", r!.Value.Tokens.RefreshToken));
    }

    [Fact]
    public async Task A_rejected_refresh_means_the_session_is_over()
    {
        var (session, _) = Create(HttpStatusCode.Unauthorized);

        Assert.Null(await session.RefreshAsync("revoked", Browser()));
    }

    [Fact]
    public void Missing_refresh_cookie_is_detected()
    {
        Assert.Null(ApiSession.ReadRefreshCookie(new HttpResponseMessage(HttpStatusCode.OK)));
    }
}

public class WebHelperTests
{
    [Theory]
    [InlineData("/projects/1", "/projects/1")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    public void Return_urls_cannot_leave_the_site(string? input, string expected)
    {
        Assert.Equal(expected, Urls.Safe(input));
    }

    [Fact]
    public void Workspace_urls_only_carry_non_default_filters()
    {
        var project = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var folder = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Assert.Equal($"/projects/{project}", new WorkspaceFilter().ToUrl(project));
        Assert.Equal(
            $"/projects/{project}?folder={folder}&sub=true&status=Failed&q=leave%20policy&sort=Name&desc=false&page=2",
            new WorkspaceFilter { Folder = folder, Sub = true, Status = DocumentStatus.Failed, Q = "leave policy", Sort = DocumentSort.Name, Desc = false, Page = 2 }.ToUrl(project));
    }

    [Fact]
    public void With_copies_instead_of_mutating()
    {
        var original = new WorkspaceFilter { Page = 3 };

        var next = original.With(f => f.Page = 4);

        Assert.Equal(3, original.Page);
        Assert.Equal(4, next.Page);
    }

    [Fact]
    public void Folder_options_have_full_paths_and_subtrees()
    {
        Guid hr = Guid.NewGuid(), leave = Guid.NewGuid(), parental = Guid.NewGuid(), finance = Guid.NewGuid();
        var tree = new[]
        {
            new FolderTreeNode(hr, null, "HR", 0, 1, [new FolderTreeNode(leave, hr, "Leave", 0, 1, [new FolderTreeNode(parental, leave, "Parental", 1, 1, [])])]),
            new FolderTreeNode(finance, null, "Finance", 0, 0, []),
        };

        var options = DocuMind.Web.ViewModels.FolderTree.Flatten(tree);

        Assert.Equal(["HR", "HR / Leave", "HR / Leave / Parental", "Finance"], options.Select(o => o.Path));
        Assert.Equal([0, 1, 2, 0], options.Select(o => o.Depth));
        Assert.Equal(new HashSet<Guid> { hr, leave, parental }, options[0].Subtree); // used to block moving a folder into itself
    }

    [Fact]
    public void Project_access_mirrors_the_server_rules()
    {
        var me = Guid.NewGuid();
        var mine = new CollectionDto(Guid.NewGuid(), Guid.NewGuid(), me, CollectionVisibility.Private, "Mine", null, 0, default, default);
        var theirsShared = mine with { OwnerId = Guid.NewGuid(), Visibility = CollectionVisibility.Project };

        var viewer = new ProjectAccess(ProjectRole.Viewer, IsAdmin: false);
        Assert.False(viewer.CanEdit);
        Assert.True(viewer.CanManage(mine, me));
        Assert.False(viewer.CanManage(theirsShared, me));

        var owner = new ProjectAccess(ProjectRole.Owner, IsAdmin: false);
        Assert.True(owner.CanManage(theirsShared, me));

        var admin = new ProjectAccess(null, IsAdmin: true);
        Assert.True(admin.IsOwner);
    }
}
