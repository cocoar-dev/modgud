using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Common;

/// <summary>
/// Base class for all entities in the domain.
/// </summary>
public abstract class Entity
{
    private readonly List<object> _pendingEvents = [];

    /// <summary>
    /// Domain events raised by mutations, not yet appended to the event stream.
    /// Drained by the store on SaveChangesAsync.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<object> PendingEvents => _pendingEvents;

    /// <summary>
    /// The unique identifier for this entity using GUIDv7.
    /// </summary>
    [JsonInclude]
    public Guid Id { get; protected set; }

    /// <summary>
    /// When this entity was created.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; protected set; }

    /// <summary>
    /// When this entity was last modified.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset? ModifiedAt { get; protected set; }

    protected Entity()
    {
        Id = GuidV7.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    protected Entity(Guid id)
    {
        Id = id;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Raises a domain event. The event is stored in <see cref="PendingEvents"/>
    /// until the store drains and appends them to the event stream.
    /// </summary>
    protected void RaiseEvent(object @event) => _pendingEvents.Add(@event);

    /// <summary>
    /// Clears all pending events. Called by stores after appending events,
    /// or in CreateAsync to discard events raised during construction.
    /// </summary>
    public void ClearPendingEvents() => _pendingEvents.Clear();

    public void MarkModified()
    {
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public override bool Equals(object? obj)
    {
        if (obj is not Entity other)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (GetType() != other.GetType())
            return false;

        return Id == other.Id;
    }

    public override int GetHashCode() => Id.GetHashCode();

    public static bool operator ==(Entity? left, Entity? right)
    {
        if (left is null && right is null)
            return true;

        if (left is null || right is null)
            return false;

        return left.Equals(right);
    }

    public static bool operator !=(Entity? left, Entity? right) => !(left == right);
}
