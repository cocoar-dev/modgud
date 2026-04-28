namespace Cocoar.Auth.Infrastructure.Persistence.Marten.Projections;

/// <summary>
/// Lightweight reference to a principal, customer, or other entity for embedding
/// in projections. Holds Id + display label. For principal references,
/// <see cref="PrincipalType"/> indicates whether the reference is a Person or Group
/// (or future principal type) so the UI can show appropriate icons / behavior.
/// Null for non-principal refs (e.g. Customer).
/// </summary>
public record ViewRef
{
    public Guid Id { get; init; }
    public string? Label { get; init; }
    public string? PrincipalType { get; init; }
}
