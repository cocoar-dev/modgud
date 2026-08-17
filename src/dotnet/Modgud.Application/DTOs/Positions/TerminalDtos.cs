using Modgud.Domain.PositionTerminals;

namespace Modgud.Application.DTOs.Positions;

/// <summary>
/// Wire shape for a <see cref="TerminalEnrollment"/> slot (MG-FT-03). The DPoP
/// key itself never leaves the server — the list only says whether the slot is
/// enrolled.
/// </summary>
public class TerminalDto
{
    public required string Id { get; set; }
    public required string PositionId { get; set; }
    public IReadOnlyList<string> AllowedPositionIds { get; set; } = [];
    public required string DisplayName { get; set; }
    public string? Location { get; set; }
    public required string ClientId { get; set; }
    public required string WebAuthnRpId { get; set; }
    public required string Binding { get; set; }
    /// <summary>Business scopes the managed client may request. Their linked
    /// OAuth API resources become staffing-token audiences.</summary>
    public IReadOnlyList<string> Scopes { get; set; } = [];
    /// <summary>Apps authorizing this managed client to request app-scoped
    /// business scopes.</summary>
    public IReadOnlyList<string> AppIds { get; set; } = [];
    public TerminalEnrollmentStatus Status { get; set; }
    public bool Enrolled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Only populated on the create response for client-secret slots.</summary>
    public string? ClientSecret { get; set; }
}

public class TerminalCreateDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Location { get; set; }

    /// <summary>The RP ID staff passkeys verify against on this terminal —
    /// typically shared across every terminal client of the consuming app
    /// (spike 3: the credential is RP-ID-scoped, not client-scoped).</summary>
    public string WebAuthnRpId { get; set; } = string.Empty;

    /// <summary>Stable open binding ID. Omitted by old clients = dpop.</summary>
    public string Binding { get; set; } = "dpop";

    /// <summary>Optional n:m assignment. The route position is included
    /// automatically; omitted by V1 clients means that singleton position.</summary>
    public IReadOnlyList<string>? AllowedPositionIds { get; set; }

    /// <summary>Business scopes whose resources become staffing audiences.</summary>
    public IReadOnlyList<string> Scopes { get; set; } = [];

    /// <summary>Apps owning the selected business scopes.</summary>
    public IReadOnlyList<string> AppIds { get; set; } = [];
}

public sealed class TerminalAllowedPositionsUpdateDto
{
    public IReadOnlyList<string> AllowedPositionIds { get; set; } = [];
}

public class TerminalUpdateDto
{
    public string? DisplayName { get; set; }
    public string? Location { get; set; }
}

/// <summary>OAuth access profile of a terminal-managed client. Kept separate
/// from physical slot details and binding/lifecycle fields.</summary>
public sealed class TerminalOAuthAccessUpdateDto
{
    /// <summary>Human-readable OAuth-client name. ClientId remains immutable.</summary>
    public string? DisplayName { get; set; }
    public IReadOnlyList<string> Scopes { get; set; } = [];
    public IReadOnlyList<string> AppIds { get; set; } = [];
}
