using Modgud.Infrastructure.OpenIddict.Cimd;

namespace Modgud.Tests.Unit.OAuth.Cimd;

/// <summary>
/// Pins the CIMD document validator: the draft-spec + v1
/// (public-only) rules a fetched metadata document must satisfy before it is
/// trusted as a client registration. The fetcher is SSRF-tested elsewhere;
/// this is the policy layer over the already-fetched bytes.
/// </summary>
public class CimdMetadataParserTests
{
    private const string ClientId = "https://app.example.com/oauth/client-metadata.json";

    private static string Doc(
        string? clientId = ClientId,
        string[]? redirectUris = null,
        string? authMethod = null,
        string[]? grantTypes = null,
        string[]? responseTypes = null,
        string? scope = null,
        string? clientName = null,
        bool includeClientSecret = false)
    {
        var fields = new List<string>();
        if (clientId is not null) fields.Add($"\"client_id\":{Json(clientId)}");
        var uris = redirectUris ?? ["https://app.example.com/callback"];
        fields.Add($"\"redirect_uris\":[{string.Join(",", uris.Select(Json))}]");
        if (authMethod is not null) fields.Add($"\"token_endpoint_auth_method\":{Json(authMethod)}");
        if (grantTypes is not null) fields.Add($"\"grant_types\":[{string.Join(",", grantTypes.Select(Json))}]");
        if (responseTypes is not null) fields.Add($"\"response_types\":[{string.Join(",", responseTypes.Select(Json))}]");
        if (scope is not null) fields.Add($"\"scope\":{Json(scope)}");
        if (clientName is not null) fields.Add($"\"client_name\":{Json(clientName)}");
        if (includeClientSecret) fields.Add("\"client_secret\":\"shhh\"");
        return "{" + string.Join(",", fields) + "}";
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static CimdMetadata AssertValid(string json, string requestedClientId = ClientId)
    {
        var result = CimdMetadataParser.Parse(json, requestedClientId);
        var valid = Assert.IsType<CimdValidationResult.Valid>(result);
        return valid.Metadata;
    }

    private static void AssertInvalid(string json, string requestedClientId = ClientId)
    {
        var result = CimdMetadataParser.Parse(json, requestedClientId);
        Assert.IsType<CimdValidationResult.Invalid>(result);
    }

    [Fact]
    public void Accepts_a_minimal_public_document()
    {
        var meta = AssertValid(Doc());
        Assert.Equal(ClientId, meta.ClientId);
        Assert.Contains("https://app.example.com/callback", meta.RedirectUris);
        Assert.Contains("authorization_code", meta.GrantTypes); // defaulted when absent
    }

    [Fact]
    public void Parses_scope_grant_types_and_client_name()
    {
        var meta = AssertValid(Doc(
            grantTypes: ["authorization_code", "refresh_token"],
            scope: "openid offline_access api.read",
            clientName: "Example MCP"));
        Assert.Equal("Example MCP", meta.ClientName);
        Assert.Equal(["authorization_code", "refresh_token"], meta.GrantTypes);
        Assert.Equal(["openid", "offline_access", "api.read"], meta.Scopes);
    }

    [Fact]
    public void Accepts_explicit_none_auth_method() => AssertValid(Doc(authMethod: "none"));

    [Fact]
    public void Accepts_http_loopback_redirect() =>
        AssertValid(Doc(redirectUris: ["http://127.0.0.1:1234/cb", "http://localhost/cb"]));

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("private_key_jwt")]
    public void Rejects_non_public_auth_methods(string method) =>
        AssertInvalid(Doc(authMethod: method));

    [Fact]
    public void Rejects_document_carrying_a_client_secret() =>
        AssertInvalid(Doc(includeClientSecret: true));

    [Fact]
    public void Rejects_client_id_mismatch() =>
        AssertInvalid(Doc(clientId: "https://attacker.example/evil"), requestedClientId: ClientId);

    [Fact]
    public void Rejects_missing_client_id() => AssertInvalid(Doc(clientId: null));

    [Fact]
    public void Rejects_missing_redirect_uris() => AssertInvalid(Doc(redirectUris: []));

    [Fact]
    public void Rejects_non_loopback_http_redirect() =>
        AssertInvalid(Doc(redirectUris: ["http://app.example.com/cb"]));

    [Fact]
    public void Rejects_disallowed_grant_type() =>
        AssertInvalid(Doc(grantTypes: ["authorization_code", "client_credentials"]));

    [Fact]
    public void Rejects_grant_types_without_authorization_code() =>
        AssertInvalid(Doc(grantTypes: ["refresh_token"]));

    [Fact]
    public void Rejects_disallowed_response_type() =>
        AssertInvalid(Doc(responseTypes: ["token"]));

    [Fact]
    public void Rejects_non_json() => AssertInvalid("this is not json");

    [Fact]
    public void Rejects_non_object_json() => AssertInvalid("[\"array\"]");
}
