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
        bool includeClientSecret = false,
        string? applicationType = null)
    {
        var fields = new List<string>();
        if (clientId is not null) fields.Add($"\"client_id\":{Json(clientId)}");
        var uris = redirectUris ?? ["https://app.example.com/callback"];
        fields.Add($"\"redirect_uris\":[{string.Join(",", uris.Select(Json))}]");
        if (authMethod is not null) fields.Add($"\"token_endpoint_auth_method\":{Json(authMethod)}");
        if (applicationType is not null) fields.Add($"\"application_type\":{Json(applicationType)}");
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
    [InlineData("web")]
    [InlineData("native")]
    public void Records_a_declared_application_type(string declared) =>
        Assert.Equal(declared, AssertValid(Doc(applicationType: declared)).ApplicationType);

    [Fact]
    public void Omitted_application_type_is_null_not_defaulted() =>
        Assert.Null(AssertValid(Doc()).ApplicationType);

    [Theory]
    [InlineData("desktop")]
    [InlineData("Native")]
    public void Rejects_unknown_application_type(string declared) =>
        AssertInvalid(Doc(applicationType: declared));

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("client_secret_jwt")]   // a shared secret too — forbidden by the draft
    [InlineData("tls_client_auth")]     // Modgud does not do mTLS client auth
    public void Rejects_shared_secret_and_unsupported_auth_methods(string method) =>
        AssertInvalid(Doc(authMethod: method));

    [Fact]
    public void Accepts_the_chatgpt_private_key_jwt_document()
    {
        var meta = AssertValid(RealWorldClientMetadata.ChatGpt, RealWorldClientMetadata.ChatGptId);
        Assert.Equal("private_key_jwt", meta.TokenEndpointAuthMethod);
        Assert.Equal("https://chatgpt.com/oauth/jwks.json", meta.JwksUri);
        Assert.Null(meta.Jwks);
    }

    private const string InlineRsaKey = """{"kty":"RSA","kid":"k1","use":"sig","n":"sXch","e":"AQAB"}""";

    private static string PrivateKeyJwtDoc(string keySource) =>
        Doc().TrimEnd('}') + ",\"token_endpoint_auth_method\":\"private_key_jwt\"" + keySource + "}";

    [Fact]
    public void Accepts_private_key_jwt_with_an_inline_key_set()
    {
        var meta = AssertValid(PrivateKeyJwtDoc($",\"jwks\":{{\"keys\":[{InlineRsaKey}]}}"));
        Assert.Null(meta.JwksUri);
        Assert.Contains("\"kid\":\"k1\"", meta.Jwks);
    }

    [Theory]
    [InlineData("")]                                                                 // no key source
    [InlineData(",\"jwks_uri\":\"https://app.example.com/jwks\",\"jwks\":{\"keys\":[]}")] // both
    [InlineData(",\"jwks_uri\":\"http://app.example.com/jwks\"")]                   // not https
    [InlineData(",\"jwks\":{\"keys\":[{\"kty\":\"RSA\",\"n\":\"x\",\"e\":\"AQAB\",\"d\":\"secret\"}]}")] // private material
    [InlineData(",\"jwks\":{\"keys\":[{\"kty\":\"RSA\",\"use\":\"enc\",\"n\":\"x\",\"e\":\"AQAB\"}]}")]  // no signing key
    public void Rejects_private_key_jwt_without_exactly_one_usable_key_source(string keySource) =>
        AssertInvalid(PrivateKeyJwtDoc(keySource));

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

    [Theory]
    [InlineData("client_credentials")]
    [InlineData("urn:ietf:params:oauth:grant-type:jwt-bearer")]
    public void Drops_grant_types_modgud_does_not_offer(string grant)
    {
        // The document describes the client for every AS; it is not an order.
        var meta = AssertValid(Doc(grantTypes: ["authorization_code", "refresh_token", grant]));
        Assert.Equal(["authorization_code", "refresh_token"], meta.GrantTypes);
    }

    [Fact]
    public void Rejects_when_only_unoffered_grants_remain() =>
        AssertInvalid(Doc(grantTypes: ["client_credentials", "refresh_token"]));

    [Fact]
    public void Rejects_grant_types_without_authorization_code() =>
        AssertInvalid(Doc(grantTypes: ["refresh_token"]));

    [Fact]
    public void Rejects_response_types_without_code() =>
        AssertInvalid(Doc(responseTypes: ["token"]));

    [Fact]
    public void Ignores_extra_response_type_alongside_code() =>
        AssertValid(Doc(responseTypes: ["code", "token"]));

    // ── Real-world documents (see RealWorldClientMetadata) ──────────────

    [Theory]
    [MemberData(nameof(RealWorldClientMetadata.PublicCimdDocuments), MemberType = typeof(RealWorldClientMetadata))]
    public void Accepts_every_real_public_client_document(string clientId, string json, string expectedName)
    {
        var meta = AssertValid(json, clientId);
        Assert.Equal(expectedName, meta.ClientName);
        Assert.Equal(["authorization_code", "refresh_token"], meta.GrantTypes);
    }

    [Fact]
    public void Claude_ai_loses_jwt_bearer_and_keeps_its_callback()
    {
        var meta = AssertValid(RealWorldClientMetadata.ClaudeAi, RealWorldClientMetadata.ClaudeAiId);
        Assert.Equal(["https://claude.ai/api/mcp/auth_callback"], meta.RedirectUris);
        Assert.Empty(meta.Scopes); // no scope → the resolver applies the realm default
    }

    [Fact]
    public void Vs_code_ported_loopback_uri_gets_its_portless_twin()
    {
        // VS Code names http://127.0.0.1:33418/ but may come back on another port.
        var meta = AssertValid(RealWorldClientMetadata.VsCode, RealWorldClientMetadata.VsCodeId);
        Assert.Equal(["http://127.0.0.1:33418/", "http://127.0.0.1/", "https://vscode.dev/redirect"], meta.RedirectUris);
        Assert.Equal("native", meta.ApplicationType);
    }

    [Fact]
    public void Drops_a_redirect_uri_form_modgud_does_not_accept()
    {
        var meta = AssertValid(Doc(redirectUris: ["com.example.app:/cb", "https://app.example.com/callback"]));
        Assert.Equal(["https://app.example.com/callback"], meta.RedirectUris);
    }

    [Fact]
    public void Rejects_when_no_redirect_uri_survives() =>
        AssertInvalid(Doc(redirectUris: ["com.example.app:/cb", "http://app.example.com/cb"]));

    [Fact]
    public void Tolerates_a_utf8_byte_order_mark() =>
        AssertValid((char)0xFEFF + Doc());

    [Fact]
    public void Rejects_non_json() => AssertInvalid("this is not json");

    [Fact]
    public void Rejects_non_object_json() => AssertInvalid("[\"array\"]");
}
