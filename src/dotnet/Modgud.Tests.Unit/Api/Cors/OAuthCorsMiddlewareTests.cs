using Modgud.Api.Cors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Modgud.Tests.Unit.Api.Cors;

/// <summary>
/// Pinning tests for <see cref="OAuthCorsMiddleware"/> — the CORS layer that lets
/// a browser-only SPA (Authorization Code + PKCE, no BFF) complete the flow.
/// Security-critical: a regression that echoes an UNREGISTERED origin on a
/// credentialed endpoint would hand any site cross-origin read access to the
/// token / userinfo responses.
/// </summary>
public class OAuthCorsMiddlewareTests
{
    private const string AllowedOrigin = "https://app.example.com";
    private const string ForeignOrigin = "https://evil.example.com";

    private static ICorsService CorsService() =>
        new CorsService(Options.Create(new CorsOptions()), NullLoggerFactory.Instance);

    private sealed class FakeOriginProvider : IClientCorsOriginProvider
    {
        private readonly HashSet<string> _allowed;
        public FakeOriginProvider(params string[] allowed) =>
            _allowed = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

        public ValueTask<bool> IsOriginAllowedAsync(string origin, CancellationToken ct) =>
            ValueTask.FromResult(_allowed.Contains(origin.TrimEnd('/')));
    }

    private static DefaultHttpContext Build(string method, string path, string? origin, bool preflight = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        if (origin is not null)
            http.Request.Headers[HeaderNames.Origin] = origin;
        if (preflight)
            http.Request.Headers[HeaderNames.AccessControlRequestMethod] = "POST";
        return http;
    }

    private static async Task<(HttpContext ctx, int nextCalls)> Run(
        HttpContext ctx, IClientCorsOriginProvider provider)
    {
        var nextCalls = 0;
        var mw = new OAuthCorsMiddleware(_ => { nextCalls++; return Task.CompletedTask; });
        await mw.InvokeAsync(ctx, CorsService(), provider);
        return (ctx, nextCalls);
    }

    private static string Acao(HttpContext ctx) =>
        ctx.Response.Headers[HeaderNames.AccessControlAllowOrigin].ToString();

    [Fact]
    public async Task Preflight_for_registered_origin_short_circuits_204_with_acao()
    {
        var ctx = Build(HttpMethods.Options, "/connect/token", AllowedOrigin, preflight: true);
        var (_, nextCalls) = await Run(ctx, new FakeOriginProvider(AllowedOrigin));

        Assert.Equal(0, nextCalls); // preflight never reaches the rest of the pipeline
        Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
        Assert.Equal(AllowedOrigin, Acao(ctx));
    }

    [Fact]
    public async Task Actual_request_for_registered_origin_gets_acao_and_continues()
    {
        var ctx = Build(HttpMethods.Post, "/connect/token", AllowedOrigin);
        var (_, nextCalls) = await Run(ctx, new FakeOriginProvider(AllowedOrigin));

        Assert.Equal(1, nextCalls);
        Assert.Equal(AllowedOrigin, Acao(ctx));
    }

    [Fact]
    public async Task Unregistered_origin_gets_no_cors_headers()
    {
        var ctx = Build(HttpMethods.Post, "/connect/token", ForeignOrigin);
        var (_, nextCalls) = await Run(ctx, new FakeOriginProvider(AllowedOrigin));

        Assert.Equal(1, nextCalls); // request still flows; the browser blocks the JS read
        Assert.Equal(string.Empty, Acao(ctx));
    }

    [Fact]
    public async Task Userinfo_endpoint_is_also_covered()
    {
        var ctx = Build(HttpMethods.Get, "/connect/userinfo", AllowedOrigin);
        var (_, nextCalls) = await Run(ctx, new FakeOriginProvider(AllowedOrigin));

        Assert.Equal(1, nextCalls);
        Assert.Equal(AllowedOrigin, Acao(ctx));
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/jwks")]
    public async Task Public_metadata_is_readable_from_any_origin(string path)
    {
        // No registered origins at all — public metadata must still be readable.
        var ctx = Build(HttpMethods.Get, path, ForeignOrigin);
        var (_, nextCalls) = await Run(ctx, new FakeOriginProvider());

        Assert.Equal(1, nextCalls);
        Assert.Equal("*", Acao(ctx));
    }

    [Fact]
    public async Task Non_oidc_path_is_untouched()
    {
        var ctx = Build(HttpMethods.Post, "/api/users", AllowedOrigin);
        var (_, nextCalls) = await Run(ctx, new FakeOriginProvider(AllowedOrigin));

        Assert.Equal(1, nextCalls);
        Assert.Equal(string.Empty, Acao(ctx)); // admin API is not part of the CORS surface
    }

    [Fact]
    public async Task Request_without_origin_header_is_untouched()
    {
        var ctx = Build(HttpMethods.Post, "/connect/token", origin: null);
        var (_, nextCalls) = await Run(ctx, new FakeOriginProvider(AllowedOrigin));

        Assert.Equal(1, nextCalls);
        Assert.Equal(string.Empty, Acao(ctx));
    }
}
