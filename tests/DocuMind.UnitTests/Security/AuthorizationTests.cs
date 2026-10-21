using DocuMind.Application.Abstractions.Messaging;
using DocuMind.Application.Abstractions.Security;
using DocuMind.Application.Common.Exceptions;
using DocuMind.Application.Common.Security;
using DocuMind.Application.Features.Auth;
using DocuMind.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DocuMind.UnitTests.Security;

public class AuthorizationBehaviorTests
{
    [RequireRole(UserRole.Manager)]
    public sealed record ManagersOnly : ICommand<string>;

    public sealed record AnyUser : IQuery<string>;

    private sealed class ManagersOnlyHandler : ICommandHandler<ManagersOnly, string>
    {
        public Task<string> Handle(ManagersOnly request, CancellationToken ct) => Task.FromResult("ok");
    }

    private sealed class AnyUserHandler : IQueryHandler<AnyUser, string>
    {
        public Task<string> Handle(AnyUser request, CancellationToken ct) => Task.FromResult("ok");
    }

    private static TestHost Host(ICurrentUser user)
    {
        var host = new TestHost(user);
        host.Services.AddScoped<IRequestHandler<ManagersOnly, string>, ManagersOnlyHandler>();
        host.Services.AddScoped<IRequestHandler<AnyUser, string>, AnyUserHandler>();
        host.Services.AddSingleton(Substitute.For<IIdentityService>());
        return host;
    }

    [Fact]
    public async Task Requests_are_denied_to_anonymous_callers_by_default()
    {
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => Host(FakeCurrentUser.Anonymous()).Sender().Send(new AnyUser()));
    }

    [Fact]
    public async Task Public_requests_are_allowed_anonymously()
    {
        var host = Host(FakeCurrentUser.Anonymous());

        // Passes authorization and reaches validation (which rejects the empty body) instead of a 401.
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => host.Sender().Send(new LoginCommand("", "")));
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Admin)]
    public async Task Role_requirements_include_higher_roles(UserRole role)
    {
        Assert.Equal("ok", await Host(FakeCurrentUser.As(role)).Sender().Send(new ManagersOnly()));
    }

    [Fact]
    public async Task Insufficient_role_is_forbidden_and_audited()
    {
        var host = Host(FakeCurrentUser.As(UserRole.User));

        await Assert.ThrowsAsync<ForbiddenException>(() => host.Sender().Send(new ManagersOnly()));

        var entry = Assert.Single(host.Audit.Entries);
        Assert.Equal("access.denied", entry.Action);
        Assert.False(entry.Succeeded);
        Assert.Equal(host.User.Id, entry.ActorId);
    }
}

public class AccessControlTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();
    private readonly Guid _collectionId = Guid.NewGuid();

    private (IAccessControl Access, TestHost Host) For(UserRole role = UserRole.User)
    {
        var host = new TestHost(FakeCurrentUser.As(role));
        host.Permissions.ProjectExistsAsync(_projectId, Arg.Any<CancellationToken>()).Returns(true);
        return (host.Get<IAccessControl>(), host);
    }

    private void GivenProjectRole(TestHost host, ProjectRole? role)
    {
        host.Permissions.GetProjectRoleAsync(host.User.Id, _projectId, Arg.Any<CancellationToken>()).Returns(role);
        host.Permissions.GetDocumentAccessAsync(host.User.Id, _documentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentAccessInfo(_documentId, _projectId, role, SharedViaCollection: false));
    }

    [Fact]
    public async Task Non_members_get_not_found_so_ids_cannot_be_probed()
    {
        var (access, host) = For();
        GivenProjectRole(host, null);

        await Assert.ThrowsAsync<NotFoundException>(() => access.RequireProjectAsync(_projectId, ProjectRole.Viewer, default));
        await Assert.ThrowsAsync<NotFoundException>(() => access.RequireDocumentAsync(_documentId, DocumentPermission.Read, default));
    }

    [Fact]
    public async Task Viewers_can_read_but_not_change_documents()
    {
        var (access, host) = For();
        GivenProjectRole(host, ProjectRole.Viewer);

        await access.RequireDocumentAsync(_documentId, DocumentPermission.Read, default);
        await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireDocumentAsync(_documentId, DocumentPermission.Write, default));
    }

    [Fact]
    public async Task Editors_cannot_manage_the_project()
    {
        var (access, host) = For();
        GivenProjectRole(host, ProjectRole.Editor);

        Assert.Equal(ProjectRole.Editor, await access.RequireProjectAsync(_projectId, ProjectRole.Editor, default));
        await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireProjectAsync(_projectId, ProjectRole.Owner, default));
    }

    [Fact]
    public async Task A_shared_collection_grants_read_but_never_write_outside_the_project()
    {
        var (access, host) = For();
        host.Permissions.GetDocumentAccessAsync(host.User.Id, _documentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentAccessInfo(_documentId, _projectId, ProjectRole: null, SharedViaCollection: true));

        await access.RequireDocumentAsync(_documentId, DocumentPermission.Read, default);
        await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireDocumentAsync(_documentId, DocumentPermission.Write, default));
    }

    [Fact]
    public async Task Admins_pass_every_check()
    {
        var (access, host) = For(UserRole.Admin);
        GivenProjectRole(host, null);

        Assert.Equal(ProjectRole.Owner, await access.RequireProjectAsync(_projectId, ProjectRole.Owner, default));
        await access.RequireDocumentAsync(_documentId, DocumentPermission.Write, default);
    }

    [Fact]
    public async Task Someone_elses_private_collection_is_invisible_even_to_a_project_owner()
    {
        var (access, host) = For();
        host.Permissions.GetCollectionAccessAsync(host.User.Id, _collectionId, Arg.Any<CancellationToken>())
            .Returns(new CollectionAccessInfo(_collectionId, _projectId, OwnerId: Guid.NewGuid(), CollectionVisibility.Private, ProjectRole.Owner, SharedWithUser: false));

        await Assert.ThrowsAsync<NotFoundException>(() => access.RequireCollectionAsync(_collectionId, CollectionPermission.View, default));
    }

    [Fact]
    public async Task Project_collections_are_visible_to_members_and_managed_by_owners()
    {
        var (access, host) = For();
        var info = new CollectionAccessInfo(_collectionId, _projectId, OwnerId: Guid.NewGuid(), CollectionVisibility.Project, ProjectRole.Viewer, SharedWithUser: false);
        host.Permissions.GetCollectionAccessAsync(host.User.Id, _collectionId, Arg.Any<CancellationToken>()).Returns(info);

        await access.RequireCollectionAsync(_collectionId, CollectionPermission.View, default);
        await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireCollectionAsync(_collectionId, CollectionPermission.Manage, default));

        host.Permissions.GetCollectionAccessAsync(host.User.Id, _collectionId, Arg.Any<CancellationToken>()).Returns(info with { ProjectRole = ProjectRole.Owner });
        await access.RequireCollectionAsync(_collectionId, CollectionPermission.Manage, default);
    }

    [Fact]
    public async Task Collection_owners_lose_control_when_they_leave_the_project()
    {
        var (access, host) = For();
        host.Permissions.GetCollectionAccessAsync(host.User.Id, _collectionId, Arg.Any<CancellationToken>())
            .Returns(new CollectionAccessInfo(_collectionId, _projectId, OwnerId: host.User.Id, CollectionVisibility.Private, ProjectRole: null, SharedWithUser: false));

        await access.RequireCollectionAsync(_collectionId, CollectionPermission.View, default);
        await Assert.ThrowsAsync<ForbiddenException>(() => access.RequireCollectionAsync(_collectionId, CollectionPermission.Manage, default));
    }
}
