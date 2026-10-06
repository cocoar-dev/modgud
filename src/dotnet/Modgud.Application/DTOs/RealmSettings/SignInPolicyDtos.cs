namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the sign-in policy sub-section of
/// <c>/api/admin/realm-settings</c> (ADR 0025). Unlike the other sections this one is
/// <c>null</c> on <see cref="RealmSettingsDto.SignIn"/> while the realm has never
/// configured it: the realm then runs under a policy derived from the deployment's
/// former settings, and the SPA says so instead of showing defaults as if they were
/// saved. Levels are <c>Single</c> / <c>Multi</c>; <c>OwnFactorNotOffered</c> is
/// <c>Ignore</c> / <c>RequireViaBrowser</c>.</summary>
public record SignInPolicyDto
{
    public string MinimumLevel { get; init; } = "Single";
    public string AdministrationMinimumLevel { get; init; } = "Multi";
    public int SetupGraceDays { get; init; } = 14;
    public bool Password { get; init; } = true;
    public bool EmailCode { get; init; }
    public bool Passkey { get; init; } = true;
    public bool Totp { get; init; } = true;
    public bool EmailAfterPassword { get; init; } = true;
    public string OwnFactorNotOffered { get; init; } = "RequireViaBrowser";
}

/// <summary>Patch payload for the sign-in policy sub-section. Each property is
/// nullable = no change on the wire; non-null = replace. A realm that never configured
/// the section is patched over <c>SignInPolicy.Defaults</c>, so the first save writes a
/// complete policy.</summary>
public record UpdateSignInPolicyDto
{
    public string? MinimumLevel { get; init; }
    public string? AdministrationMinimumLevel { get; init; }
    public int? SetupGraceDays { get; init; }
    public bool? Password { get; init; }
    public bool? EmailCode { get; init; }
    public bool? Passkey { get; init; }
    public bool? Totp { get; init; }
    public bool? EmailAfterPassword { get; init; }
    public string? OwnFactorNotOffered { get; init; }
}
