namespace Cocoar.Auth.Domain.Common;

/// <summary>
/// Generates GUIDv7 (time-ordered) identifiers.
/// </summary>
public static class GuidV7
{
    /// <summary>
    /// Creates a new GUIDv7 based on the current UTC timestamp.
    /// </summary>
    public static Guid NewGuid() => Guid.CreateVersion7();

    /// <summary>
    /// Creates a new GUIDv7 based on a specific timestamp.
    /// </summary>
    public static Guid NewGuid(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
