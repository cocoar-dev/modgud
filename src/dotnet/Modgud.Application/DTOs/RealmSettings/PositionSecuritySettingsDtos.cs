using Modgud.Authorization.Principals;

namespace Modgud.Application.DTOs.RealmSettings;

public record PositionSecuritySettingsDto
{
    public ProofCapability? RequiredProofCapabilities { get; init; }
    public BindingCapability? RequiredBindingCapabilities { get; init; }
}

public record UpdatePositionSecuritySettingsDto
{
    public ProofCapability? RequiredProofCapabilities { get; init; }
    public BindingCapability? RequiredBindingCapabilities { get; init; }
}

public record PositionSecurityConsequencesDto
{
    public IReadOnlyList<PositionSecurityAffectedPositionDto> Positions { get; init; } = [];
    public IReadOnlyList<string> TerminalIds { get; init; } = [];
    public IReadOnlyList<string> StaffingSessionIds { get; init; } = [];
    public bool HasConsequences => Positions.Count > 0 || TerminalIds.Count > 0 || StaffingSessionIds.Count > 0;
}

public record PositionSecurityAffectedPositionDto(
    string Id,
    string AccountName,
    IReadOnlyList<string> ViolatingActivationProofs,
    IReadOnlyList<string> ViolatingDeviceBindings);
