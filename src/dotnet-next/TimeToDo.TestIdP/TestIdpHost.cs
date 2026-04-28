using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using TimeToDo.TestIdP.Config;

namespace TimeToDo.TestIdP;

/// <summary>
/// Builds a fully-configured TestIdP <see cref="WebApplication"/>. Shared
/// between the standalone <c>Program.cs</c> (which reads JSON + runs it) and
/// integration tests (which pass a programmatic config + start on a dynamic
/// port). Isolating the wire-up here keeps both callers in sync.
/// </summary>
public static class TestIdpHost
{
    public static WebApplication Build(TestIdpConfig config, string[]? args = null)
        => Build(config, port: null, args);

    /// <summary>
    /// Builds a TestIdP <see cref="WebApplication"/>. When <paramref name="port"/>
    /// is set, Kestrel binds explicitly to <c>127.0.0.1:port</c> — this is how
    /// tests pin to a pre-allocated free port. When null, appsettings.json drives
    /// the binding (standalone dev mode).
    /// </summary>
    public static WebApplication Build(TestIdpConfig config, int? port, string[]? args)
    {
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        Uri? explicitIssuer = null;
        if (port.HasValue)
        {
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(System.Net.IPAddress.Loopback, port.Value);
            });
            // Kestrel listening on 127.0.0.1 serves requests under either
            // 127.0.0.1 or localhost, but the issuer in the discovery doc
            // must match the host the OIDC client uses — otherwise the
            // client's issuer validation fails at token-exchange time.
            explicitIssuer = new Uri($"http://127.0.0.1:{port.Value}/");
        }
        else
        {
            // Dockerized deployments (E2E). The container's internal listener is
            // on port 5000, but the URL the OIDC client sees must be reachable
            // from both the browser (host) and other containers (Docker network).
            // TESTIDP_ISSUER lets the test harness inject a shared address like
            // http://host.docker.internal:15000/ that resolves identically from
            // both sides on Docker Desktop (Win/Mac/Linux with --add-host).
            var envIssuer = Environment.GetEnvironmentVariable("TESTIDP_ISSUER");
            if (!string.IsNullOrWhiteSpace(envIssuer) && Uri.TryCreate(envIssuer, UriKind.Absolute, out var parsed))
                explicitIssuer = parsed;
        }

        builder.Services.AddSingleton(config);

        // Unique InMemory db per WebApplication instance so parallel tests
        // don't stomp on each other's client registrations.
        var dbName = $"TestIdPStore-{Guid.NewGuid():N}";
        builder.Services.AddDbContext<TestIdpDbContext>(options =>
        {
            options.UseInMemoryDatabase(dbName);
            options.UseOpenIddict();
        });

        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.Cookie.Name = "TestIdP.Session";
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.SlidingExpiration = false;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });

        builder.Services.AddAuthorization();

        builder.Services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore().UseDbContext<TestIdpDbContext>();
            })
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("/authorize")
                       .SetTokenEndpointUris("/token")
                       .SetUserInfoEndpointUris("/userinfo")
                       .SetEndSessionEndpointUris("/logout");

                if (explicitIssuer is not null)
                    options.SetIssuer(explicitIssuer);

                options.AllowAuthorizationCodeFlow()
                       .RequireProofKeyForCodeExchange();

                options.RegisterScopes(
                    OpenIddictConstants.Scopes.OpenId,
                    OpenIddictConstants.Scopes.Profile,
                    OpenIddictConstants.Scopes.Email,
                    "groups", "roles");

                options.AddEphemeralEncryptionKey()
                       .AddEphemeralSigningKey();

                options.DisableAccessTokenEncryption();

                options.UseAspNetCore()
                       .EnableAuthorizationEndpointPassthrough()
                       .EnableTokenEndpointPassthrough()
                       .EnableUserInfoEndpointPassthrough()
                       .EnableEndSessionEndpointPassthrough()
                       .DisableTransportSecurityRequirement();

                // Diagnostic: log token-request rejections so tests can see
                // what OpenIddict refused (invalid_grant + reason).
                options.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ProcessErrorContext>(b =>
                    b.UseInlineHandler(ctx =>
                    {
                        TestIdpLog.Write(
                            $"[ProcessError] endpoint={ctx.EndpointType} error={ctx.Error} description={ctx.ErrorDescription} uri={ctx.ErrorUri}");
                        return default;
                    }));
            });

        builder.Services.AddHostedService<SeedClientsHostedService>();

        var app = builder.Build();

        // Trace every incoming request into the test log so tests can see
        // the full OIDC dialogue.
        app.Use(async (context, next) =>
        {
            var start = DateTime.UtcNow;
            TestIdpLog.Write($"→ {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
            try
            {
                await next();
                TestIdpLog.Write($"← {context.Response.StatusCode} {context.Request.Method} {context.Request.Path} ({(DateTime.UtcNow - start).TotalMilliseconds:F0}ms)");
            }
            catch (Exception ex)
            {
                TestIdpLog.Write($"! {ex.GetType().Name}: {ex.Message} on {context.Request.Path}");
                throw;
            }
        });

        app.UseAuthentication();
        app.UseAuthorization();

        LoginEndpoints.Map(app);
        AuthorizationEndpoints.Map(app);
        AdminEndpoints.Map(app);

        app.MapGet("/", (TestIdpConfig c) =>
            Results.Content(HomePage.Render(c), "text/html; charset=utf-8"));

        return app;
    }

    /// <summary>
    /// Adds a redirect URI to a registered client at runtime. Needed for
    /// integration tests, which create IdpConfigs with fresh GUIDs after the
    /// test host has already started — the redirect URI would otherwise be
    /// unknown to OpenIddict.
    /// </summary>
    public static async Task AddRedirectUriAsync(IServiceProvider services, string clientId, string uri, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var config = scope.ServiceProvider.GetRequiredService<TestIdpConfig>();
        var application = await manager.FindByClientIdAsync(clientId, ct)
            ?? throw new InvalidOperationException($"Client '{clientId}' not registered.");

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;

        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application, ct);
        if (descriptor.RedirectUris.Contains(parsed)) return;

        descriptor.RedirectUris.Add(parsed);
        // After Populate, ClientSecret holds the stored one-way hash — feeding
        // that back to UpdateAsync would silently rehash it and break client
        // auth. Restore the plaintext from our config so UpdateAsync rehashes
        // the correct secret.
        var clientConfig = config.Clients.FirstOrDefault(c => c.ClientId == clientId);
        descriptor.ClientSecret = clientConfig?.ClientSecret;
        await manager.UpdateAsync(application, descriptor, ct);
    }
}
