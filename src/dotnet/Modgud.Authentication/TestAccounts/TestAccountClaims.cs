namespace Modgud.Authentication.TestAccounts;

/// <summary>
/// ADR 0026 — the claim that tells every app a test account apart. Emitted as JSON
/// <c>true</c> in the ID token, every access token and the userinfo response, for every
/// client and scope set; absent for any other account.
/// </summary>
public static class TestAccountClaims
{
    public const string Type = "modgud.test_account";
}
