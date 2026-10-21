using DocuMind.Domain.Common;
using DocuMind.Domain.Projects;
using DocuMind.Domain.Security;

namespace DocuMind.UnitTests.Domain;

public class MembershipTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly Guid _owner = Guid.NewGuid();

    [Fact]
    public void The_creator_becomes_the_first_owner()
    {
        var project = Project.Create("HR", null, _owner, Now);

        Assert.Equal(ProjectRole.Owner, project.RoleOf(_owner));
        Assert.Equal(_owner, project.CreatedBy);
    }

    [Fact]
    public void The_last_owner_can_neither_leave_nor_be_demoted()
    {
        var project = Project.Create("HR", null, _owner, Now);

        Assert.Throws<DomainException>(() => project.RemoveMember(_owner, Now));
        Assert.Throws<DomainException>(() => project.ChangeMemberRole(_owner, ProjectRole.Editor, Now));
    }

    [Fact]
    public void An_owner_can_leave_once_someone_else_is_owner()
    {
        var project = Project.Create("HR", null, _owner, Now);
        var other = Guid.NewGuid();
        project.AddMember(other, ProjectRole.Editor, _owner, Now);
        project.ChangeMemberRole(other, ProjectRole.Owner, Now);

        project.RemoveMember(_owner, Now);

        Assert.Null(project.RoleOf(_owner));
        Assert.Equal(ProjectRole.Owner, project.RoleOf(other));
    }

    [Fact]
    public void A_user_is_a_member_at_most_once()
    {
        var project = Project.Create("HR", null, _owner, Now);

        Assert.Throws<DomainException>(() => project.AddMember(_owner, ProjectRole.Viewer, _owner, Now));
    }

    [Fact]
    public void Collections_are_shared_with_users_other_than_their_owner()
    {
        var collection = Collection.Create(Guid.NewGuid(), _owner, "Board pack", null, CollectionVisibility.Private, Now);
        var reader = Guid.NewGuid();

        Assert.Throws<DomainException>(() => collection.ShareWith(_owner, _owner, Now));

        collection.ShareWith(reader, _owner, Now);
        Assert.True(collection.IsSharedWith(reader));
        Assert.Throws<DomainException>(() => collection.ShareWith(reader, _owner, Now));

        Assert.True(collection.Unshare(reader, Now));
        Assert.False(collection.IsSharedWith(reader));
    }
}
