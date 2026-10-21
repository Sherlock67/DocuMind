namespace DocuMind.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected init; } = Guid.CreateVersion7();
}

/// <summary>Marker for aggregate roots. Only aggregate roots get repositories on the write side.</summary>
public interface IAggregateRoot;
