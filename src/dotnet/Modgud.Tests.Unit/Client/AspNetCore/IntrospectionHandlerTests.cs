using System.Net;
using System.Security.Claims;
using Modgud.Client.AspNetCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Modgud.Tests.Unit.Client.AspNetCore;

/// <summary>
/// Pins <see cref="ModgudTokenIntrospection"/> — the reference-token
/// validation path (#132). Introspection <em>is</em> the validation here, so
/// the contract is <b>fail-closed</b>: only an active, audience-valid token
/// yields a principal; everything else (inactive, non-2xx, transport error,
/// foreign audience, malformed body) rejects.
///
/// <para>The IdP-side companion pin
/// (<c>UserInfoPerAudienceTests.Introspection_Carries_ResourceAccess_Only_For_Audience_Or_Presenter_Client</c>)
/// proves the real endpoint returns <c>resource_access</c> to an audience
/// client; these tests pin how the lib projects that response.</para>
/// </summary>
public class IntrospectionHandlerTests
{
    private const string Authority = "https://auth.example.com";
    private const string Audience = "https://mcp.acme.example";

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            if (respond is null)
                throw new InvalidOperationException(
                    "Modgud introspection made an HTTP call the test did not expect.");
            return respond(request);
        }
    }

    private static ModgudReferenceTokenOptions Options(
        string audience = Audience, string? clientId = null, string secret = "rs-secret")
        => new()
        {
            Authority = Authority,
            Audience = audience,
            IntrospectionClientId = clientId,
            IntrospectionClientSecret = secret,
        };

    private static async Task<(ClaimsPrincipal? Principal, StubHttpMessageHandler Handler)> IntrospectAsync(
        ModgudReferenceTokenOptions options, StubHttpMessageHandler handler, string token = "opaque-ref-token")
    {
        var original = ModgudTokenIntrospection.SharedClient;
        ModgudTokenIntrospection.SharedClient = new HttpClient(handler);
        try
        {
            var principal = await ModgudTokenIntrospection.IntrospectAsync(
                options, token, "ModgudIntrospection", NullLogger.Instance, CancellationToken.None);
            return (principal, handler);
        }
        finally
        {
            ModgudTokenIntrospection.SharedClient = original;
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task Active_token_yields_principal_with_resource_access_and_standard_claims()
    {
        var body = """{"active":true,"sub":"u1","name":"Alice","scope":"openid permissions","aud":["https://mcp.acme.example","some-client"],"resource_access":{"https://mcp.acme.example":{"permissions":["policy:write"],"roles":["Editor"]}}}""";
        var (principal, _) = await IntrospectAsync(Options(), new StubHttpMessageHandler(_ => Json(body)));

        Assert.NotNull(principal);
        var identity = (ClaimsIdentity)principal!.Identity!;
        Assert.True(identity.IsAuthenticated);
        Assert.Equal("u1", identity.FindFirst("sub")?.Value);
        Assert.Equal("u1", identity.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Alice", identity.Name);   // nameType "name"
        // The load-bearing claim survives verbatim for the transformation.
        var rawResourceAccess = identity.FindFirst(ModgudClaimsTransformation.ResourceAccessClaimType)?.Value ?? "";
        Assert.Contains("""{"permissions":["policy:write"],"roles":["Editor"]}""", rawResourceAccess);
    }

    [Fact]
    public async Task Resource_access_flows_through_the_shared_claims_transformation()
    {
        var body = """{"active":true,"sub":"u1","aud":"https://mcp.acme.example","resource_access":{"https://mcp.acme.example":{"permissions":["policy:write"],"roles":["Editor"]}}}""";
        var (principal, _) = await IntrospectAsync(Options(), new StubHttpMessageHandler(_ => Json(body)));

        var transform = new ModgudClaimsTransformation(Microsoft.Extensions.Options.Options.Create(
            new ModgudOptions { Authority = Authority, Audience = Audience }));
        var transformed = await transform.TransformAsync(principal!);

        Assert.Contains(transformed.FindAll(ModgudClaimsTransformation.PermissionClaimType),
            c => c.Value == "policy:write");
        Assert.Contains(transformed.FindAll(ClaimTypes.Role), c => c.Value == "Editor");
    }

    [Fact]
    public async Task Introspection_request_uses_form_body_client_credentials()
    {
        var body = $$"""{"active":true,"aud":"{{Audience}}"}""";
        var (_, handler) = await IntrospectAsync(
            Options(secret: "rs-secret"), new StubHttpMessageHandler(_ => Json(body)), token: "the-token");

        Assert.Equal($"{Authority}/connect/introspect", handler.LastRequestUri!.ToString());
        var form = handler.LastRequestBody!;
        Assert.Contains("token=the-token", form);
        Assert.Contains($"client_id={Uri.EscapeDataString(Audience)}", form);
        Assert.Contains("client_secret=rs-secret", form);
    }

    [Fact]
    public async Task Client_id_defaults_to_audience_but_can_be_overridden()
    {
        var body = $$"""{"active":true,"aud":"{{Audience}}"}""";
        var (_, handler) = await IntrospectAsync(
            Options(clientId: "custom-introspector"), new StubHttpMessageHandler(_ => Json(body)));

        Assert.Contains("client_id=custom-introspector", handler.LastRequestBody!);
    }

    [Fact]
    public async Task Inactive_token_is_rejected()
    {
        var (principal, _) = await IntrospectAsync(
            Options(), new StubHttpMessageHandler(_ => Json("""{"active":false}""")));
        Assert.Null(principal);
    }

    [Fact]
    public async Task Active_token_for_a_different_audience_is_rejected()
    {
        // active:true but the token isn't for us — defence in depth against a
        // misconfigured introspection client id.
        var body = """{"active":true,"aud":["https://other-rs.example.com"],"resource_access":{"https://other-rs.example.com":{"permissions":["policy:write"]}}}""";
        var (principal, _) = await IntrospectAsync(Options(), new StubHttpMessageHandler(_ => Json(body)));
        Assert.Null(principal);
    }

    [Fact]
    public async Task Non_success_status_is_rejected_fail_closed()
    {
        var (principal, handler) = await IntrospectAsync(
            Options(), new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Null(principal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Transport_failure_is_rejected_fail_closed()
    {
        var (principal, _) = await IntrospectAsync(
            Options(), new StubHttpMessageHandler(_ => throw new HttpRequestException("boom")));
        Assert.Null(principal);
    }

    [Fact]
    public async Task Malformed_json_is_rejected()
    {
        var (principal, _) = await IntrospectAsync(
            Options(), new StubHttpMessageHandler(_ => Json("not json")));
        Assert.Null(principal);
    }
}
