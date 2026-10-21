using DocuMind.Domain.Common;
using DocuMind.Domain.Security;

namespace DocuMind.Domain.Projects;

/// <summary>
/// A curated, cross-cutting set of documents within a project (e.g. "Q3 board pack").
/// Unlike folders, a document can be in any number of collections.
/// Private collections are visible only to their owner and the users they are shared with;
/// sharing a collection also grants read access to the documents in it.
/// </summary>
public sealed class Collection : Entity, IAggregateRoot
{
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 1000;

    private readonly List<CollectionItem> _items = [];
    private readonly List<CollectionShare> _shares = [];

    private Collection() { } // EF Core

    public Guid ProjectId { get; private set; }
    public Guid OwnerId { get; private set; }
    public CollectionVisibility Visibility { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<CollectionItem> Items => _items.AsReadOnly();
    public IReadOnlyCollection<CollectionShare> Shares => _shares.AsReadOnly();

    public static Collection Create(Guid projectId, Guid ownerId, string name, string? description, CollectionVisibility visibility, DateTimeOffset now) => new()
    {
        ProjectId = projectId,
        OwnerId = ownerId,
        Visibility = visibility,
        Name = Names.Require(name, MaxNameLength, "Collection name"),
        Description = Names.Optional(description, MaxDescriptionLength),
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Update(string name, string? description, CollectionVisibility visibility, DateTimeOffset now)
    {
        Name = Names.Require(name, MaxNameLength, "Collection name");
        Description = Names.Optional(description, MaxDescriptionLength);
        Visibility = visibility;
        UpdatedAt = now;
    }

    public bool IsSharedWith(Guid userId) => _shares.Any(s => s.UserId == userId);

    /// <summary>Adds documents (already verified to be in this project). Existing members are ignored.</summary>
    public int AddDocuments(IEnumerable<Guid> documentIds, DateTimeOffset now)
    {
        var existing = _items.Select(i => i.DocumentId).ToHashSet();
        var added = 0;
        foreach (var id in documentIds.Where(existing.Add))
        {
            _items.Add(new CollectionItem(Id, id, now));
            added++;
        }

        if (added > 0) UpdatedAt = now;
        return added;
    }

    public bool RemoveDocument(Guid documentId, DateTimeOffset now)
    {
        var removed = _items.RemoveAll(i => i.DocumentId == documentId) > 0;
        if (removed) UpdatedAt = now;
        return removed;
    }

    public void ShareWith(Guid userId, Guid sharedBy, DateTimeOffset now)
    {
        if (userId == OwnerId) throw new DomainException("The owner already has access to this collection.");
        if (IsSharedWith(userId)) throw new DomainException("The collection is already shared with that user.");

        _shares.Add(new CollectionShare(Id, userId, sharedBy, now));
        UpdatedAt = now;
    }

    public bool Unshare(Guid userId, DateTimeOffset now)
    {
        var removed = _shares.RemoveAll(s => s.UserId == userId) > 0;
        if (removed) UpdatedAt = now;
        return removed;
    }
}

/// <summary>Membership of a document in a collection.</summary>
public sealed class CollectionItem
{
    private CollectionItem() { } // EF Core

    internal CollectionItem(Guid collectionId, Guid documentId, DateTimeOffset addedAt)
    {
        CollectionId = collectionId;
        DocumentId = documentId;
        AddedAt = addedAt;
    }

    public Guid CollectionId { get; private set; }
    public Guid DocumentId { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
}

/// <summary>Grants a user read access to a collection and the documents in it.</summary>
public sealed class CollectionShare
{
    private CollectionShare() { } // EF Core

    internal CollectionShare(Guid collectionId, Guid userId, Guid sharedBy, DateTimeOffset sharedAt)
    {
        CollectionId = collectionId;
        UserId = userId;
        SharedBy = sharedBy;
        SharedAt = sharedAt;
    }

    public Guid CollectionId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid SharedBy { get; private set; }
    public DateTimeOffset SharedAt { get; private set; }
}
