using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modgud.AspNetCore.ResourceServer;

namespace Modgud.Tests.Unit.ResourceServer;

public class ResourceServerRegistrationTests
{
    [Fact]
    public async Task Default_mode_registers_one_public_jwt_scheme()
    {
        var services = Services();
        services.AddModgudResourceServer(options =>
        {
            options.Authority = "https://id.example.com";
            options.Audience = "api";
        });
        using var provider = services.BuildServiceProvider();

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var publicScheme = await schemes.GetSchemeAsync(
            ModgudResourceServerDefaults.AuthenticationScheme);

        Assert.Equal(typeof(JwtBearerHandler), publicScheme?.HandlerType);
        Assert.Null(await schemes.GetSchemeAsync(ModgudSchemeNames.Introspection));
        Assert.Null(provider.GetService<IHttpClientFactory>());
    }

    [Fact]
    public async Task Reference_only_mode_registers_one_public_introspection_scheme()
    {
        var services = Services();
        services.AddModgudResourceServer(options =>
        {
            options.Authority = "https://id.example.com";
            options.Audience = "api";
            options.TokenMode = ModgudTokenMode.OnlyReferenceToken;
            options.IntrospectionClientSecret = "secret";
        });
        using var provider = services.BuildServiceProvider();

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var publicScheme = await schemes.GetSchemeAsync(
            ModgudResourceServerDefaults.AuthenticationScheme);

        Assert.Equal(typeof(ModgudIntrospectionHandler), publicScheme?.HandlerType);
        Assert.Null(await schemes.GetSchemeAsync(ModgudSchemeNames.Jwt));
        Assert.NotNull(provider.GetService<IHttpClientFactory>());
    }

    [Fact]
    public async Task Both_mode_is_one_public_policy_scheme_with_two_internal_validators()
    {
        var services = Services();
        services.AddModgudResourceServer(options =>
        {
            options.Authority = "https://id.example.com";
            options.Audience = "api";
            options.TokenMode = ModgudTokenMode.Both;
            options.IntrospectionClientSecret = "secret";
        });
        using var provider = services.BuildServiceProvider();

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(
            typeof(PolicySchemeHandler),
            (await schemes.GetSchemeAsync(ModgudResourceServerDefaults.AuthenticationScheme))
            ?.HandlerType);
        Assert.Equal(
            typeof(JwtBearerHandler),
            (await schemes.GetSchemeAsync(ModgudSchemeNames.Jwt))?.HandlerType);
        Assert.Equal(
            typeof(ModgudIntrospectionHandler),
            (await schemes.GetSchemeAsync(ModgudSchemeNames.Introspection))?.HandlerType);

        var transformations = services
            .Where(x => x.ServiceType == typeof(IClaimsTransformation))
            .ToArray();
        Assert.All(
            transformations,
            registration => Assert.Equal(
                "NoopClaimsTransformation",
                registration.ImplementationType?.Name));
    }

    [Theory]
    [InlineData("Bearer aaa.bbb.ccc", ModgudSchemeNames.Jwt)]
    [InlineData("DPoP aaa.bbb.ccc", ModgudSchemeNames.Jwt)]
    [InlineData("Bearer opaque_reference_token", ModgudSchemeNames.Introspection)]
    [InlineData("DPoP opaque_reference_token", ModgudSchemeNames.Introspection)]
    [InlineData("Bearer one.dot", ModgudSchemeNames.Introspection)]
    [InlineData("Bearer one.two.three.four", ModgudSchemeNames.Introspection)]
    [InlineData(null, ModgudSchemeNames.Jwt)]
    [InlineData("Basic abc", ModgudSchemeNames.Jwt)]
    public void Both_mode_routes_by_modgud_token_shape(string? header, string expectedScheme)
    {
        Assert.Equal(expectedScheme, ServiceCollectionExtensions.SelectTokenScheme(header));
    }

    [Fact]
    public async Task Jwt_projection_uses_the_resource_servers_single_audience()
    {
        var services = Services();
        services.AddModgudResourceServer(options =>
        {
            options.Authority = "https://id.example.com";
            options.Audience = "api-a";
            options.TokenMode = ModgudTokenMode.Both;
            options.IntrospectionClientSecret = "secret";
        });
        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        const string resourceAccess = """
            {
              "api-a": { "permissions": ["a:read"] },
              "api-b": { "permissions": ["b:read"] }
            }
            """;
        var principal = Principal(resourceAccess);

        await monitor.Get(ModgudSchemeNames.Jwt).Events.OnTokenValidated(
            Context(provider, principal, ModgudSchemeNames.Jwt));

        Assert.Equal(
            ["a:read"],
            principal.FindAll(ModgudClaimTypes.Permission).Select(x => x.Value));
    }

    [Fact]
    public void A_second_modgud_registration_is_rejected()
    {
        var services = Services();
        services.AddModgudResourceServer(ValidJwt);

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddModgudResourceServer(ValidJwt));

        Assert.Contains("only be called once", error.Message);
    }

    [Theory]
    [InlineData(ModgudTokenMode.OnlyReferenceToken)]
    [InlineData(ModgudTokenMode.Both)]
    public void Reference_accepting_modes_require_a_secret(ModgudTokenMode mode)
    {
        var services = Services();

        Assert.Throws<OptionsValidationException>(() =>
            services.AddModgudResourceServer(options =>
            {
                options.Authority = "https://id.example.com";
                options.Audience = "api";
                options.TokenMode = mode;
            }));
    }

    [Fact]
    public void Missing_audience_realm_path_and_insecure_authority_are_rejected()
    {
        AssertInvalid(options =>
        {
            options.Authority = "https://id.example.com";
            options.Audience = "";
        });
        AssertInvalid(options =>
        {
            options.Authority = "https://id.example.com/system";
            options.Audience = "api";
        });
        AssertInvalid(options =>
        {
            options.Authority = "http://id.example.com";
            options.Audience = "api";
        });
    }

    [Fact]
    public void Only_jwt_rejects_irrelevant_introspection_credentials()
    {
        AssertInvalid(options =>
        {
            options.Authority = "https://id.example.com";
            options.Audience = "api";
            options.IntrospectionClientSecret = "unused";
        });
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        return services;
    }

    private static void ValidJwt(ModgudResourceServerOptions options)
    {
        options.Authority = "https://id.example.com";
        options.Audience = "api";
    }

    private static void AssertInvalid(Action<ModgudResourceServerOptions> configure)
    {
        var services = Services();
        Assert.Throws<OptionsValidationException>(() =>
            services.AddModgudResourceServer(configure));
    }

    private static ClaimsPrincipal Principal(string resourceAccess)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ModgudClaimTypes.ResourceAccess, resourceAccess)],
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private static TokenValidatedContext Context(
        IServiceProvider services,
        ClaimsPrincipal principal,
        string schemeName)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme(schemeName, null, typeof(JwtBearerHandler));
        return new TokenValidatedContext(http, scheme, new JwtBearerOptions())
        {
            Principal = principal,
        };
    }
}
