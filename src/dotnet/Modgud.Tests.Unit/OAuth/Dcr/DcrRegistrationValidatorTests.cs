using Modgud.Application.Dcr;
using Modgud.Domain.Realms;

namespace Modgud.Tests.Unit.OAuth.Dcr;

/// <summary>
/// Pins every reject path on <see cref="DcrRegistrationValidator"/> plus
/// the happy path. The validator is pure — no DB, no rate-limit state —
/// so these run in single-digit ms and stay green even when the project
/// is rebuilt with a cold IDE.
/// </summary>
public class DcrRegistrationValidatorTests
{
    private static readonly DcrRegistrationValidator Sut = new();

    private static DcrSettings Settings(params string[]? reservedNames) =>
        new() { Enabled = true, ReservedNames = reservedNames is { Length: > 0 } ? reservedNames : null };

    private static DcrRegistrationRequest ValidRequest() => new()
    {
        ClientName = "Test Client",
        RedirectUris = new() { "https://example.com/callback" },
    };

    [Fact]
    public void Happy_path_returns_allow_with_dcr_prefixed_client_id()
    {
        var result = Sut.Validate(ValidRequest(), Settings(), "1.2.3.4");
        var allow = Assert.IsType<DcrValidationResult.Allow>(result);
        Assert.StartsWith("dcr-", allow.Normalized.ClientId);
        Assert.Equal("Test Client", allow.Normalized.DisplayName);
        Assert.Equal("public", allow.Normalized.ClientType);
        Assert.True(allow.Normalized.RequireConsent);
        Assert.False(allow.Normalized.AllowRememberConsent);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("native")]
    public void Declared_application_type_is_recorded_as_declared(string declared)
    {
        var req = ValidRequest() with { ApplicationType = declared };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(declared, allow.Normalized.ApplicationType);
    }

    [Fact]
    public void Omitted_application_type_stays_null_the_effective_type_is_settled_at_read_time()
    {
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(ValidRequest(), Settings(), "ip"));
        Assert.Null(allow.Normalized.ApplicationType);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("Web")]       // OIDC DCR: literal lowercase values
    [InlineData("")]
    public void Unknown_application_type_is_invalid_client_metadata(string declared)
    {
        var req = ValidRequest() with { ApplicationType = declared };
        var reject = Assert.IsType<DcrValidationResult.Reject>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(DcrErrorCodes.InvalidClientMetadata, reject.ErrorCode);
        Assert.Equal(DcrRejectionReason.InvalidApplicationType, reject.Reason);
    }

    [Fact]
    public void Missing_redirect_uris_rejected_as_missing_redirect()
    {
        var req = ValidRequest() with { RedirectUris = null };
        var reject = Assert.IsType<DcrValidationResult.Reject>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(DcrErrorCodes.InvalidRedirectUri, reject.ErrorCode);
        Assert.Equal(DcrRejectionReason.MissingRedirectUri, reject.Reason);
    }

    [Fact]
    public void Empty_redirect_uris_rejected_as_missing_redirect()
    {
        var req = ValidRequest() with { RedirectUris = new() };
        var reject = Assert.IsType<DcrValidationResult.Reject>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(DcrRejectionReason.MissingRedirectUri, reject.Reason);
    }

    [Theory]
    [InlineData("http://example.com/cb")]                  // plain http on non-loopback
    [InlineData("com.example.app://callback")]             // custom URI scheme
    [InlineData("ftp://example.com/cb")]                   // wrong scheme
    [InlineData("https://example.com/cb#fragment")]        // fragment not allowed
    [InlineData("not-a-uri")]                              // not absolute
    [InlineData("")]                                       // empty
    public void Bad_redirect_uri_rejected(string uri)
    {
        var req = ValidRequest() with { RedirectUris = new() { uri } };
        var reject = Assert.IsType<DcrValidationResult.Reject>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(DcrErrorCodes.InvalidRedirectUri, reject.ErrorCode);
        Assert.Equal(DcrRejectionReason.InvalidRedirectUri, reject.Reason);
    }

    [Theory]
    [InlineData("https://example.com/cb")]
    [InlineData("http://localhost/cb")]
    [InlineData("http://localhost:8080/cb")]
    [InlineData("http://127.0.0.1/cb")]
    [InlineData("http://127.0.0.1:8081/cb")]
    [InlineData("http://[::1]/cb")]
    [InlineData("http://[::1]:9000/cb")]
    public void Good_redirect_uri_accepted(string uri)
    {
        var req = ValidRequest() with { RedirectUris = new() { uri } };
        Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
    }

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    public void Confidential_auth_method_accepted_as_confidential_client_with_secret(string method)
    {
        var req = ValidRequest() with { TokenEndpointAuthMethod = method };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(method, allow.TokenEndpointAuthMethod);
        Assert.Equal("confidential", allow.Normalized.ClientType);
        Assert.True(allow.Normalized.RequireClientSecret);
        // Secret is left unset so CreateClientAsync mints one.
        Assert.Null(allow.Normalized.ClientSecret);
    }

    [Theory]
    [InlineData("private_key_jwt")]  // advertised by the AS, but DCR needs a JWKS we don't accept
    [InlineData("tls_client_auth")]
    [InlineData("bogus")]
    public void Unsupported_token_endpoint_auth_method_rejected(string method)
    {
        var req = ValidRequest() with { TokenEndpointAuthMethod = method };
        var reject = Assert.IsType<DcrValidationResult.Reject>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(DcrErrorCodes.InvalidClientMetadata, reject.ErrorCode);
        Assert.Equal(DcrRejectionReason.InvalidTokenAuthMethod, reject.Reason);
    }

    [Fact]
    public void Token_endpoint_auth_method_defaults_to_none_public_when_omitted()
    {
        var req = ValidRequest() with { TokenEndpointAuthMethod = null };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal("none", allow.TokenEndpointAuthMethod);
        Assert.Equal("public", allow.Normalized.ClientType);
        Assert.False(allow.Normalized.RequireClientSecret);
    }

    [Theory]
    [InlineData("client_credentials")]
    [InlineData("password")]
    [InlineData("implicit")]
    [InlineData("urn:ietf:params:oauth:grant-type:device_code")]
    [InlineData("urn:ietf:params:oauth:grant-type:jwt-bearer")] // claude.ai lists it
    public void Unoffered_grant_type_is_dropped_not_fatal(string grant)
    {
        // RFC 7591 §3.2.1: the server registers what it offers and echoes it.
        var req = ValidRequest() with { GrantTypes = new() { "authorization_code", grant, "refresh_token" } };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(new[] { "authorization_code", "refresh_token" }, allow.Normalized.AllowedGrantTypes);
    }

    [Theory]
    [InlineData("client_credentials")]
    [InlineData("refresh_token")]
    public void Grant_types_without_authorization_code_rejected(string grant)
    {
        var req = ValidRequest() with { GrantTypes = new() { grant } };
        var reject = Assert.IsType<DcrValidationResult.Reject>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(DcrRejectionReason.InvalidGrantType, reject.Reason);
    }

    [Fact]
    public void Authorization_code_and_refresh_token_grants_accepted()
    {
        var req = ValidRequest() with { GrantTypes = new() { "authorization_code", "refresh_token" } };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(new[] { "authorization_code", "refresh_token" }, allow.Normalized.AllowedGrantTypes);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("id_token")]
    [InlineData("code id_token")]
    public void Disallowed_response_type_rejected(string responseType)
    {
        var req = ValidRequest() with { ResponseTypes = new() { responseType } };
        var reject = Assert.IsType<DcrValidationResult.Reject>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(DcrRejectionReason.InvalidResponseType, reject.Reason);
    }

    [Fact]
    public void Extra_response_type_alongside_code_is_ignored()
    {
        var req = ValidRequest() with { ResponseTypes = new() { "code", "token" } };
        Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
    }

    // ── Real-world registration bodies (see RealWorldClientMetadata) ────

    private static DcrRegistrationRequest Body(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<DcrRegistrationRequest>(json)!;

    [Theory]
    [MemberData(nameof(RealWorldClientMetadata.DcrBodies), MemberType = typeof(RealWorldClientMetadata))]
    public void Accepts_every_real_client_registration(string client, string json)
    {
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(Body(json), Settings(), "ip"));
        Assert.Equal(client, allow.Normalized.DisplayName);
    }

    [Fact]
    public void Zed_ephemeral_port_registration_also_matches_the_next_port()
    {
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(Body(RealWorldClientMetadata.ZedDcr), Settings(), "ip"));
        Assert.Equal(new[] { "http://127.0.0.1:49152/callback", "http://127.0.0.1/callback" }, allow.Normalized.RedirectUris);
    }

    [Fact]
    public void Drops_a_redirect_uri_form_modgud_does_not_accept()
    {
        var req = ValidRequest() with { RedirectUris = new() { "com.example.app:/cb", "https://example.com/callback" } };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(new[] { "https://example.com/callback" }, allow.Normalized.RedirectUris);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Missing_client_name_falls_back_to_the_redirect_host(string? name)
    {
        // RFC 7591 §2: client_name is optional — its absence never fails.
        var req = ValidRequest() with { ClientName = name, RedirectUris = new() { "https://mcp-client.example/cb" } };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal("mcp-client.example", allow.Normalized.DisplayName);
    }

    [Fact]
    public void Missing_client_name_on_a_loopback_only_client_gets_a_neutral_label()
    {
        var req = ValidRequest() with { ClientName = null, RedirectUris = new() { "http://127.0.0.1/cb" } };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal("Unnamed application", allow.Normalized.DisplayName);
    }

    [Fact]
    public void Client_name_over_80_chars_is_truncated()
    {
        var req = ValidRequest() with { ClientName = new string('a', 81) };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(new string('a', 80), allow.Normalized.DisplayName);
    }

    [Fact]
    public void Client_name_at_80_chars_accepted()
    {
        var req = ValidRequest() with { ClientName = new string('a', 80) };
        Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
    }

    [Theory]
    [InlineData("Café Client")]      // ü, é etc. — Latin-1 supplement, OK
    [InlineData("Foo Bar 123")]      // ASCII + digits
    [InlineData("Test (v2)")]        // punctuation
    public void Latin1_or_ascii_client_name_accepted(string name)
    {
        var req = ValidRequest() with { ClientName = name };
        Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
    }

    [Theory]
    [InlineData("Тест Клиент")]      // Cyrillic
    [InlineData("テスト")]            // Japanese
    [InlineData("Аpple")]            // Cyrillic А confused with Latin A
    [InlineData("Foo😀")]            // Emoji
    public void Non_latin1_client_name_is_never_displayed_but_does_not_fail(string name)
    {
        // Confusable defence: the name is replaced, the registration stands.
        var req = ValidRequest() with { ClientName = name, RedirectUris = new() { "https://mcp-client.example/cb" } };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal("mcp-client.example", allow.Normalized.DisplayName);
    }

    [Fact]
    public void Reserved_name_substring_match_rejected_case_insensitive()
    {
        var req = ValidRequest() with { ClientName = "Cocoar Helper" };
        var reject = Assert.IsType<DcrValidationResult.Reject>(
            Sut.Validate(req, Settings("Cocoar"), "ip"));
        Assert.Equal(DcrRejectionReason.ClientNameReservedName, reject.Reason);
    }

    [Fact]
    public void Reserved_name_with_different_case_still_rejected()
    {
        var req = ValidRequest() with { ClientName = "ANTHROPIC AGENT" };
        var reject = Assert.IsType<DcrValidationResult.Reject>(
            Sut.Validate(req, Settings("anthropic"), "ip"));
        Assert.Equal(DcrRejectionReason.ClientNameReservedName, reject.Reason);
    }

    [Fact]
    public void Empty_reserved_names_list_does_not_match_anything()
    {
        var req = ValidRequest() with { ClientName = "Anything Goes" };
        Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
    }

    [Fact]
    public void Empty_string_inside_reserved_names_list_skipped()
    {
        // Defensive: a sloppy admin save with a blank entry mustn't
        // accidentally match every client name.
        var req = ValidRequest() with { ClientName = "Anything Goes" };
        Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings("", "Cocoar"), "ip"));
    }

    [Fact]
    public void Normalised_settings_token_lifetimes_propagate_into_normalised_dto()
    {
        var settings = new DcrSettings
        {
            Enabled = true,
            AccessTokenLifetime = TimeSpan.FromMinutes(7),
            RefreshTokenLifetime = TimeSpan.FromDays(3),
        };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(ValidRequest(), settings, "ip"));
        Assert.Equal(420, allow.Normalized.AccessTokenLifetime); // seconds
        Assert.Equal(3 * 86400, allow.Normalized.SlidingRefreshTokenLifetime);
    }

    [Fact]
    public void Scope_string_split_on_spaces_into_distinct_list()
    {
        var req = ValidRequest() with { Scope = "openid profile  openid email" };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Equal(new[] { "openid", "profile", "email" }, allow.Normalized.Scopes);
    }

    [Fact]
    public void Null_scope_yields_empty_list()
    {
        var req = ValidRequest() with { Scope = null };
        var allow = Assert.IsType<DcrValidationResult.Allow>(Sut.Validate(req, Settings(), "ip"));
        Assert.Empty(allow.Normalized.Scopes);
    }
}
