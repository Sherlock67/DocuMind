using DocuMind.Domain.Common;
using DocuMind.Domain.Projects;

namespace DocuMind.UnitTests.Domain;

public class FolderAndCollectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly Guid ProjectId = Guid.NewGuid();

    //   a
    //   └─ b
    //      └─ c
    //   d
    private static (Folder A, Folder B, Folder C, Folder D, List<Folder> All) Tree()
    {
        var a = Folder.Create(ProjectId, null, "a", Now);
        var b = Folder.Create(ProjectId, a, "b", Now);
        var c = Folder.Create(ProjectId, b, "c", Now);
        var d = Folder.Create(ProjectId, null, "d", Now);
        return (a, b, c, d, [a, b, c, d]);
    }

    [Fact]
    public void A_folder_cannot_be_moved_into_its_own_subtree()
    {
        var (a, _, c, _, all) = Tree();

        Assert.Throws<DomainException>(() => a.MoveTo(c, all, Now));
        Assert.Throws<DomainException>(() => a.MoveTo(a, all, Now));
    }

    [Fact]
    public void A_folder_can_move_to_another_branch_or_the_root()
    {
        var (_, b, _, d, all) = Tree();

        b.MoveTo(d, all, Now);
        Assert.Equal(d.Id, b.ParentId);

        b.MoveTo(null, all, Now);
        Assert.Null(b.ParentId);
    }

    [Fact]
    public void Folders_cannot_move_across_projects()
    {
        var (a, _, _, _, all) = Tree();
        var foreign = Folder.Create(Guid.NewGuid(), null, "x", Now);

        Assert.Throws<DomainException>(() => a.MoveTo(foreign, [.. all, foreign], Now));
    }

    [Fact]
    public void Subtree_and_depth_helpers()
    {
        var (a, b, c, d, all) = Tree();

        Assert.Equal([a.Id, b.Id, c.Id], FolderTree.SelfAndDescendants(a.Id, all));
        Assert.Equal(3, FolderTree.Depth(c.Id, all));
        Assert.Equal(3, FolderTree.Height(a.Id, all));
        Assert.Equal(1, FolderTree.Height(d.Id, all));
        Assert.True(FolderTree.IsDescendantOf(c.Id, a.Id, all));
        Assert.False(FolderTree.IsDescendantOf(a.Id, c.Id, all));
    }

    [Fact]
    public void Moving_a_subtree_respects_the_maximum_depth()
    {
        var chain = new List<Folder> { Folder.Create(ProjectId, null, "0", Now) };
        for (var i = 1; i < Folder.MaxDepth; i++)
            chain.Add(Folder.Create(ProjectId, chain[^1], i.ToString(), Now));

        var (a, _, _, _, all) = Tree(); // a has height 3

        Assert.Throws<DomainException>(() => a.MoveTo(chain[^1], [.. chain, .. all], Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    public void Folder_names_are_validated(string name)
    {
        Assert.Throws<DomainException>(() => Folder.Create(ProjectId, null, name, Now));
    }

    [Fact]
    public void Collections_ignore_documents_they_already_contain()
    {
        var collection = Collection.Create(ProjectId, Guid.NewGuid(), "Board pack", null, DocuMind.Domain.Security.CollectionVisibility.Private, Now);
        var doc = Guid.NewGuid();

        Assert.Equal(1, collection.AddDocuments([doc], Now));
        Assert.Equal(1, collection.AddDocuments([doc, Guid.NewGuid()], Now));
        Assert.Equal(2, collection.Items.Count);

        Assert.True(collection.RemoveDocument(doc, Now));
        Assert.False(collection.RemoveDocument(doc, Now));
    }
}
