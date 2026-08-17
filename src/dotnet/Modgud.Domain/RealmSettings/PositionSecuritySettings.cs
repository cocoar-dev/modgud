using Modgud.Authorization.Principals;

namespace Modgud.Domain.RealmSettings;

/// <summary>
/// Realm-wide capability floors for position activation and terminal binding.
/// Null means no floor for that dimension. Concrete method/binding IDs remain
/// position and slot choices, respectively.
/// </summary>
public sealed record PositionSecuritySettings
{
    public ProofCapability? RequiredProofCapabilities { get; init; }
    public BindingCapability? RequiredBindingCapabilities { get; init; }
}
