namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// Test category constants for filtering test runs.
///
/// Usage:
///   [Trait("Category", TestCategories.Smoke)]
///
/// Running:
///   dotnet test --filter "Category=Smoke"                    # Quick smoke tests
///   dotnet test --filter "Category=Auth|Category=OAuth"      # Specific areas
///   dotnet test --filter "Category!=MultiTenancy"            # Exclude slow tests
///   dotnet test                                               # All tests
/// </summary>
public static class TestCategories
{
    /// <summary>Startup, health check, DB connection, seed data (~10 tests, &lt;5s)</summary>
    public const string Smoke = "Smoke";

    /// <summary>Login, registration, password, profile, lockout (~40 tests)</summary>
    public const string Auth = "Auth";

    /// <summary>TOTP, Email OTP, WebAuthn (~33 tests)</summary>
    public const string TwoFactor = "TwoFactor";

    /// <summary>OAuth flows, tokens, consent, device code (~30 tests)</summary>
    public const string OAuth = "OAuth";

    /// <summary>Admin CRUD: users, roles, OAuth clients/scopes/APIs, login providers, realms (~75 tests)</summary>
    public const string Admin = "Admin";

    /// <summary>External login providers, WireMock OIDC flow (~20 tests)</summary>
    public const string ExternalLogin = "ExternalLogin";

    /// <summary>Realm routing, isolation, issuer (~20 tests)</summary>
    public const string MultiTenancy = "MultiTenancy";

    /// <summary>GDPR export, deletion, masking, user lifecycle (~23 tests)</summary>
    public const string GDPR = "GDPR";
}
