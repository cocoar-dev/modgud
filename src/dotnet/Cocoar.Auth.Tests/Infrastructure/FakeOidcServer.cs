using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Cocoar.Auth.Tests.Infrastructure;

/// <summary>
/// In-process fake OIDC server using WireMock.Net.
/// Stubs the 3 endpoints our OidcProtocolService calls:
///   1. /.well-known/openid-configuration (Discovery)
///   2. /.well-known/jwks (Signing Keys)
///   3. /token (Code → Token Exchange)
/// </summary>
public sealed class FakeOidcServer : IDisposable
{
	private readonly WireMockServer _server;
	private readonly RSA _rsa;
	private readonly RsaSecurityKey _signingKey;
	private readonly string _keyId;

	public string Authority => _server.Url!;
	public string ClientId => "fake-client-id";
	public string ClientSecret => "fake-client-secret";

	public FakeOidcServer()
	{
		_rsa = RSA.Create(2048);
		_signingKey = new RsaSecurityKey(_rsa) { KeyId = "test-key-1" };
		_keyId = _signingKey.KeyId;

		_server = WireMockServer.Start();

		SetupDiscoveryEndpoint();
		SetupJwksEndpoint();
	}

	/// <summary>
	/// Configures the /token endpoint to return an ID token for the given user.
	/// Call this before triggering the callback in your test.
	/// </summary>
	public void SetupTokenEndpoint(
		string subject,
		string? email = null,
		bool emailVerified = true,
		string? name = null,
		string? givenName = null,
		string? familyName = null,
		string? preferredUsername = null,
		string? nonce = null)
	{
		var idToken = BuildIdToken(subject, email, emailVerified, name, givenName, familyName, preferredUsername, nonce);

		var tokenResponse = JsonSerializer.Serialize(new
		{
			id_token = idToken,
			access_token = "fake-access-token",
			token_type = "Bearer",
			expires_in = 3600
		});

		// Reset and re-register token endpoint (allows calling multiple times per test)
		_server.Given(
			Request.Create()
				.WithPath("/token")
				.UsingPost()
		).RespondWith(
			Response.Create()
				.WithStatusCode(200)
				.WithHeader("Content-Type", "application/json")
				.WithBody(tokenResponse)
		);
	}

	/// <summary>
	/// Returns the configuration dictionary to use when creating a LoginProvider.
	/// </summary>
	public Dictionary<string, string> GetProviderConfiguration()
	{
		return new Dictionary<string, string>
		{
			["Authority"] = Authority,
			["ClientId"] = ClientId,
			["ClientSecret"] = ClientSecret,
			["Scopes"] = "openid profile email"
		};
	}

	/// <summary>
	/// Extracts the state and nonce from a redirect URL (from GET /external-login).
	/// </summary>
	public static (string state, string nonce) ExtractStateAndNonce(string redirectUrl)
	{
		var uri = new Uri(redirectUrl);
		var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
		return (query["state"]!, query["nonce"]!);
	}

	private void SetupDiscoveryEndpoint()
	{
		var discovery = JsonSerializer.Serialize(new
		{
			issuer = Authority,
			authorization_endpoint = $"{Authority}/authorize",
			token_endpoint = $"{Authority}/token",
			jwks_uri = $"{Authority}/.well-known/jwks",
			response_types_supported = new[] { "code" },
			subject_types_supported = new[] { "public" },
			id_token_signing_alg_values_supported = new[] { "RS256" }
		});

		_server.Given(
			Request.Create()
				.WithPath("/.well-known/openid-configuration")
				.UsingGet()
		).RespondWith(
			Response.Create()
				.WithStatusCode(200)
				.WithHeader("Content-Type", "application/json")
				.WithBody(discovery)
		);
	}

	private void SetupJwksEndpoint()
	{
		var parameters = _rsa.ExportParameters(false);
		var jwks = JsonSerializer.Serialize(new
		{
			keys = new[]
			{
				new
				{
					kty = "RSA",
					use = "sig",
					kid = _keyId,
					alg = "RS256",
					n = Base64UrlEncode(parameters.Modulus!),
					e = Base64UrlEncode(parameters.Exponent!)
				}
			}
		});

		_server.Given(
			Request.Create()
				.WithPath("/.well-known/jwks")
				.UsingGet()
		).RespondWith(
			Response.Create()
				.WithStatusCode(200)
				.WithHeader("Content-Type", "application/json")
				.WithBody(jwks)
		);
	}

	private string BuildIdToken(
		string subject,
		string? email,
		bool emailVerified,
		string? name,
		string? givenName,
		string? familyName,
		string? preferredUsername,
		string? nonce)
	{
		var claims = new List<Claim>
		{
			new("sub", subject),
		};

		if (email is not null) claims.Add(new Claim("email", email));
		if (emailVerified) claims.Add(new Claim("email_verified", "true"));
		if (name is not null) claims.Add(new Claim("name", name));
		if (givenName is not null) claims.Add(new Claim("given_name", givenName));
		if (familyName is not null) claims.Add(new Claim("family_name", familyName));
		if (preferredUsername is not null) claims.Add(new Claim("preferred_username", preferredUsername));
		if (nonce is not null) claims.Add(new Claim("nonce", nonce));

		var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
		var token = new JwtSecurityToken(
			issuer: Authority,
			audience: ClientId,
			claims: claims,
			notBefore: DateTime.UtcNow.AddMinutes(-5),
			expires: DateTime.UtcNow.AddHours(1),
			signingCredentials: credentials);

		return new JwtSecurityTokenHandler().WriteToken(token);
	}

	private static string Base64UrlEncode(byte[] bytes)
	{
		return Convert.ToBase64String(bytes)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	public void Dispose()
	{
		_server.Dispose();
		_rsa.Dispose();
	}
}
