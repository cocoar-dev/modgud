using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modgud.Authentication.Api.ExternalAuth;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// Directly exercises <see cref="HostAwareOpenIdConnectHandler.ShouldHandleRequestAsync"/>
/// — the per-tenant tiebreaker that lets two realms register the same slug-based
/// callback path (<c>/signin-oidc/{slug}</c>) without the host-blind framework
/// matcher routing a callback to the wrong realm's scheme.
/// <para>
/// No running host needed: the handler is instantiated with a stub options
/// monitor + a hand-built <see cref="DefaultHttpContext"/>. The scheme name is
/// globally unique (<c>Oidc_{guid}</c>); the realm a scheme belongs to comes
/// from <see cref="OidcSchemeRealmRegistry"/>, and the current request's realm
/// from <c>HttpContext.Items</c> (set by RealmMiddleware in production).
/// </para>
/// </summary>
public class HostAwareOidcHandlerTests
{
    private const string CallbackPath = "/signin-oidc/shared-slug";

    private sealed class StubOptionsMonitor(OpenIdConnectOptions options)
        : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => options;
        public OpenIdConnectOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }

    private static DefaultHttpContext ContextFor(string path, string? currentRealm)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (currentRealm is not null)
            ctx.Items[TenantConstants.HttpContextTenantIdKey] = currentRealm;
        return ctx;
    }

    private static async Task<HostAwareOpenIdConnectHandler> BuildHandlerAsync(
        string schemeName,
        OidcSchemeRealmRegistry registry,
        HttpContext context)
    {
        var options = new OpenIdConnectOptions { CallbackPath = CallbackPath };
        var handler = new HostAwareOpenIdConnectHandler(
            new StubOptionsMonitor(options),
            NullLoggerFactory.Instance,
            HtmlEncoder.Default,
            UrlEncoder.Default,
            registry);
        var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(HostAwareOpenIdConnectHandler));
        await handler.InitializeAsync(scheme, context);
        return handler;
    }

    [Fact]
    public async Task Handles_callback_when_request_realm_matches_scheme_realm()
    {
        var registry = new OidcSchemeRealmRegistry();
        registry.Set("Oidc_acme", "acme");

        var handler = await BuildHandlerAsync(
            "Oidc_acme", registry, ContextFor(CallbackPath, currentRealm: "acme"));

        Assert.True(await handler.ShouldHandleRequestAsync());
    }

    [Fact]
    public async Task Declines_callback_when_request_realm_differs_from_scheme_realm()
    {
        // The core scenario: two realms registered the same slug. The globex
        // scheme must NOT claim a callback that arrived on the acme realm —
        // even though the path matches — so the acme scheme can handle it.
        var registry = new OidcSchemeRealmRegistry();
        registry.Set("Oidc_globex", "globex");

        var handler = await BuildHandlerAsync(
            "Oidc_globex", registry, ContextFor(CallbackPath, currentRealm: "acme"));

        Assert.False(await handler.ShouldHandleRequestAsync());
    }

    [Fact]
    public async Task Declines_when_path_does_not_match_regardless_of_realm()
    {
        var registry = new OidcSchemeRealmRegistry();
        registry.Set("Oidc_acme", "acme");

        var handler = await BuildHandlerAsync(
            "Oidc_acme", registry, ContextFor("/something-else", currentRealm: "acme"));

        Assert.False(await handler.ShouldHandleRequestAsync());
    }

    [Fact]
    public async Task Falls_back_to_base_when_scheme_is_untracked()
    {
        // Defence-in-depth: a scheme with no realm mapping (shouldn't happen for
        // dynamically-registered schemes) falls back to the base path-only
        // behaviour rather than silently swallowing the callback.
        var registry = new OidcSchemeRealmRegistry(); // empty — Oidc_orphan not registered

        var handler = await BuildHandlerAsync(
            "Oidc_orphan", registry, ContextFor(CallbackPath, currentRealm: "acme"));

        Assert.True(await handler.ShouldHandleRequestAsync());
    }
}
