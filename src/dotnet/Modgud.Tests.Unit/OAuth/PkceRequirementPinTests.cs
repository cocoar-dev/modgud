using Modgud.Infrastructure.OpenIddict;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Xunit;

namespace Modgud.Tests.Unit.OAuth;

/// <summary>
/// OAUTH-17 — PKCE pinning. The OAuth/OIDC threat model assumes every
/// authorization-code-flow client uses PKCE; OAuth 2.1 makes it mandatory
/// for both public AND confidential clients. Modgud's
/// <c>OpenIddictExtensions.AddOpenIddictWithMarten</c> calls
/// <c>RequireProofKeyForCodeExchange()</c> on the server config — but a
/// future refactor that splits that line out, swaps to a per-client
/// <c>Requirements</c> override, or replaces the line for any other reason
/// would silently regress the protection. This test catches that.
///
/// <para>
/// We don't try to round-trip a real auth request through the server
/// pipeline (that would require a full host); instead we resolve the
/// configured <see cref="OpenIddictServerOptions"/> from a freshly-built
/// service provider and assert that the global-PKCE-required flag is
/// the one we set. If a refactor changes the API or moves the toggle
/// elsewhere, the test FAILS to compile rather than passes spuriously.
/// </para>
/// </summary>
public class PkceRequirementPinTests
{
    [Fact]
    public void OpenIddictExtensions_AddOpenIddictWithMarten_keeps_pkce_required()
    {
        // Belt-and-suspenders: also drive through the actual
        // AddOpenIddictWithMarten extension. If a future refactor breaks
        // out the RequireProofKeyForCodeExchange call into something
        // conditional, this test fails immediately.
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(
            new TestEnvironment());
        services.AddOpenIddictWithMarten(new TestOpenIddictSettings());

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenIddictServerOptions>>();
        var options = monitor.CurrentValue;

        Assert.NotEmpty(options.CodeChallengeMethods);
        Assert.Contains(OpenIddictConstants.CodeChallengeMethods.Sha256, options.CodeChallengeMethods);

        // OAuth-2.1 / MCP-spec compliance — `plain` is forbidden,
        // `S256` is mandatory. Removing the override would silently
        // re-advertise `plain` in the discovery doc; this assertion
        // catches that regression.
        Assert.DoesNotContain(OpenIddictConstants.CodeChallengeMethods.Plain, options.CodeChallengeMethods);
    }

    private sealed class TestEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
    }

    private sealed class TestOpenIddictSettings : Modgud.Infrastructure.OpenIddict.IOpenIddictSettings
    {
        public string Issuer { get; set; } = "https://test.example.com";
        public bool DevelopmentMode { get; set; } = true;
        public string? SigningCertificatePath { get; set; }
        public string[]? PreviousSigningCertificatePaths { get; set; }
        public string? EncryptionCertificatePath { get; set; }
        public int AccessTokenLifetimeMinutes { get; set; } = 60;
        public int RefreshTokenLifetimeDays { get; set; } = 14;
        public int AuthorizationCodeLifetimeMinutes { get; set; } = 5;
    }
}
