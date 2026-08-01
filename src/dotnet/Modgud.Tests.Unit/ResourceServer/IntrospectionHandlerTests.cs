using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.AspNetCore.ResourceServer;

namespace Modgud.Tests.Unit.ResourceServer;

public class IntrospectionHandlerTests
{
    private const string Authority = "https://auth.example.com";
    private const string Audience = "https://mcp.example.com";

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            if (respond is null)
                throw new InvalidOperationException("Unexpected introspection call.");
            return respond(request);
        }
    }

    private static ModgudIntrospectionOptions Options(
        string audience = Audience,
        string? clientId = null,
        string secret = "rs-secret")
        => new()
        {
            Authority = Authority,
            Audience = audience,
            ClientId = clientId ?? audience,
            ClientSecret = secret,
        };

    private static async Task<(ClaimsPrincipal? Principal, StubHttpMessageHandler Handler)> IntrospectAsync(
        ModgudIntrospectionOptions options,
        StubHttpMessageHandler handler,
        string token = "opaque-reference-token")
    {
        var principal = await ModgudTokenIntrospection.IntrospectAsync(
            new HttpClient(handler),
            options,
            token,
            "ModgudIntrospection",
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        return (principal, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task Active_token_yields_audience_projected_principal()
    {
        var body = """{"active":true,"sub":"u1","name":"Alice","scope":"openid permissions","aud":["https://mcp.example.com","some-client"],"resource_access":{"https://mcp.example.com":{"permissions":["policy:write"],"roles":["Editor"]}}}""";

        var (principal, _) = await IntrospectAsync(
            Options(),
            new StubHttpMessageHandler(_ => Json(body)));

        Assert.NotNull(principal);
        Assert.Equal("u1", principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Alice", principal.Identity!.Name);
        Assert.Contains(principal.FindAll(ModgudClaimTypes.Permission), x => x.Value == "policy:write");
        Assert.Contains(principal.FindAll(ClaimTypes.Role), x => x.Value == "Editor");
    }

    [Fact]
    public async Task Request_uses_form_body_credentials_and_audience_as_default_client_id()
    {
        var body = $$"""{"active":true,"aud":"{{Audience}}"}""";

        var (_, handler) = await IntrospectAsync(
            Options(),
            new StubHttpMessageHandler(_ => Json(body)),
            token: "the-token");

        Assert.Equal($"{Authority}/connect/introspect", handler.LastRequestUri!.ToString());
        Assert.Contains("token=the-token", handler.LastRequestBody!);
        Assert.Contains($"client_id={Uri.EscapeDataString(Audience)}", handler.LastRequestBody!);
        Assert.Contains("client_secret=rs-secret", handler.LastRequestBody!);
    }

    [Fact]
    public async Task Client_id_can_be_overridden()
    {
        var body = $$"""{"active":true,"aud":"{{Audience}}"}""";

        var (_, handler) = await IntrospectAsync(
            Options(clientId: "custom-introspector"),
            new StubHttpMessageHandler(_ => Json(body)));

        Assert.Contains("client_id=custom-introspector", handler.LastRequestBody!);
    }

    [Theory]
    [InlineData("""{"active":false}""")]
    [InlineData("""{"active":true,"aud":"another-api"}""")]
    [InlineData("not-json")]
    public async Task Inactive_foreign_or_malformed_tokens_are_rejected(string responseBody)
    {
        var (principal, _) = await IntrospectAsync(
            Options(),
            new StubHttpMessageHandler(_ => Json(responseBody)));

        Assert.Null(principal);
    }

    [Fact]
    public async Task Non_success_and_transport_failures_are_rejected()
    {
        var (nonSuccess, _) = await IntrospectAsync(
            Options(),
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var (transportFailure, _) = await IntrospectAsync(
            Options(),
            new StubHttpMessageHandler(_ => throw new HttpRequestException("boom")));

        Assert.Null(nonSuccess);
        Assert.Null(transportFailure);
    }
}
