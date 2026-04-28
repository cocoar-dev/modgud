namespace TimeToDo.TestIdP.Config;

/// <summary>
/// Root config for the TestIdP. Loaded from JSON at startup — the operator
/// controls everything (which clients may connect, which test users exist,
/// what claims they carry) by editing <c>data/test-idp-config.json</c> or
/// pointing <c>TESTIDP_CONFIG</c> at a different file.
/// </summary>
public class TestIdpConfig
{
    public List<TestIdpClient> Clients { get; set; } = [];
    public List<TestIdpUser> Users { get; set; } = [];
}

public class TestIdpClient
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    /// <summary>
    /// Redirect URI roots that are allowed. A request's redirect_uri matches
    /// if it starts with any of these (prefix match). This is intentionally
    /// loose — the TestIdP accepts any IdpConfigId path under a registered
    /// host so you don't have to pre-register every GUID.
    /// </summary>
    public List<string> RedirectUris { get; set; } = [];
}

public class TestIdpUser
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary>
    /// Claims to emit for this user. Values are either scalars (string/number/bool)
    /// or arrays. Arrays become multi-valued claims on the issued token, which
    /// mirrors how Entra/Okta emit <c>groups</c>/<c>roles</c>.
    /// </summary>
    public Dictionary<string, object> Claims { get; set; } = new();
}
