using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Modgud.AspNetCore.ResourceServer;

/// <summary>Authentication registration for Modgud resource servers.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the single Modgud resource-server scheme. The selected
    /// <see cref="ModgudResourceServerOptions.TokenMode"/> determines whether
    /// JWTs, reference tokens, or both are accepted.
    /// </summary>
    public static AuthenticationBuilder AddModgudResourceServer(
        this IServiceCollection services,
        Action<ModgudResourceServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(registration =>
                registration.ServiceType == typeof(ModgudResourceServerRegistrationMarker)))
        {
            throw new InvalidOperationException(
                "AddModgudResourceServer can only be called once. Select OnlyJwt, " +
                "OnlyReferenceToken, or Both through ModgudResourceServerOptions.TokenMode.");
        }

        var options = new ModgudResourceServerOptions();
        configure(options);
        Validate(options);

        services.AddSingleton<ModgudResourceServerRegistrationMarker>();
        services.AddAuthorization();

        var authentication = services.AddAuthentication(
            ModgudResourceServerDefaults.AuthenticationScheme);

        switch (options.TokenMode)
        {
            case ModgudTokenMode.OnlyJwt:
                AddJwt(authentication, ModgudResourceServerDefaults.AuthenticationScheme, options);
                break;

            case ModgudTokenMode.OnlyReferenceToken:
                AddIntrospection(
                    authentication,
                    ModgudResourceServerDefaults.AuthenticationScheme,
                    options);
                break;

            case ModgudTokenMode.Both:
                authentication.AddPolicyScheme(
                    ModgudResourceServerDefaults.AuthenticationScheme,
                    displayName: null,
                    policy =>
                    {
                        policy.ForwardDefaultSelector = context =>
                            SelectTokenScheme(context.Request.Headers.Authorization);
                    });
                AddJwt(authentication, ModgudSchemeNames.Jwt, options);
                AddIntrospection(authentication, ModgudSchemeNames.Introspection, options);
                break;

            default:
                throw new InvalidOperationException("Unsupported Modgud token mode.");
        }

        return authentication;
    }

    internal static bool LooksLikeModgudJwt(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !AuthenticationHeaderValue.TryParse(authorizationHeader, out var header) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(header.Scheme, Dpop.DpopResource.Scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var token = header.Parameter;
        var firstDot = token.IndexOf('.');
        if (firstDot <= 0) return false;
        var secondDot = token.IndexOf('.', firstDot + 1);
        return secondDot > firstDot + 1 &&
               secondDot < token.Length - 1 &&
               token.IndexOf('.', secondDot + 1) < 0;
    }

    internal static string SelectTokenScheme(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !AuthenticationHeaderValue.TryParse(authorizationHeader, out var header) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(header.Scheme, Dpop.DpopResource.Scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return ModgudSchemeNames.Jwt;
        }

        return LooksLikeModgudJwt(authorizationHeader)
            ? ModgudSchemeNames.Jwt
            : ModgudSchemeNames.Introspection;
    }

    private static void AddJwt(
        AuthenticationBuilder authentication,
        string scheme,
        ModgudResourceServerOptions resourceServer)
    {
        authentication.AddJwtBearer(scheme, options =>
        {
            resourceServer.ConfigureJwtBearer?.Invoke(options);
            options.Authority = resourceServer.Authority;
            options.Audience = resourceServer.Audience;
            options.RequireHttpsMetadata = resourceServer.RequireHttpsMetadata;
            WireJwtEvents(options, resourceServer.Audience);
        });

        authentication.Services.AddOptions<JwtBearerOptions>(scheme)
            .Validate(
                options => options.EventsType is null,
                "EventsType is not supported. Configure callbacks through " +
                "ModgudResourceServerOptions.ConfigureJwtBearer and JwtBearerOptions.Events.")
            .ValidateOnStart();
    }

    private static void AddIntrospection(
        AuthenticationBuilder authentication,
        string scheme,
        ModgudResourceServerOptions resourceServer)
    {
        authentication.Services.AddHttpClient(ModgudHttpClientNames.Introspection);
        authentication.AddScheme<ModgudIntrospectionOptions, ModgudIntrospectionHandler>(
            scheme,
            options =>
            {
                options.Authority = resourceServer.Authority;
                options.Audience = resourceServer.Audience;
                options.ClientId = string.IsNullOrWhiteSpace(resourceServer.IntrospectionClientId)
                    ? resourceServer.Audience
                    : resourceServer.IntrospectionClientId;
                options.ClientSecret = resourceServer.IntrospectionClientSecret!;
            });
    }

    private static void WireJwtEvents(JwtBearerOptions options, string audience)
    {
        options.Events ??= new JwtBearerEvents();

        var existingMessageReceived = options.Events.OnMessageReceived;
        options.Events.OnMessageReceived = async context =>
        {
            if (existingMessageReceived is not null)
                await existingMessageReceived(context);

            if (context.Result is null &&
                string.IsNullOrEmpty(context.Token) &&
                ModgudDpopJwtBearer.ExtractDpopSchemeToken(context.HttpContext.Request) is { } token)
            {
                context.Token = token;
            }
        };

        var existingTokenValidated = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            if (existingTokenValidated is not null)
                await existingTokenValidated(context);
            if (context.Result is not null) return;

            ModgudDpopJwtBearer.EnforceBinding(context);
            if (context.Result is null)
                ModgudClaimsProjector.Project(context.Principal, audience);
        };
    }

    private static void Validate(ModgudResourceServerOptions options)
    {
        var failures = new List<string>();

        if (!Enum.IsDefined(options.TokenMode))
            failures.Add("TokenMode must be OnlyJwt, OnlyReferenceToken, or Both.");
        if (string.IsNullOrWhiteSpace(options.Authority))
            failures.Add("Authority is required.");
        else if (!IsValidAuthority(options.Authority, options.RequireHttpsMetadata))
        {
            failures.Add(
                "Authority must be an absolute HTTP(S) realm host root without a path, " +
                "query, or fragment. HTTPS is required unless RequireHttpsMetadata=false.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
            failures.Add("Audience is required.");

        var acceptsReferenceTokens =
            options.TokenMode is ModgudTokenMode.OnlyReferenceToken or ModgudTokenMode.Both;
        if (acceptsReferenceTokens &&
            string.IsNullOrWhiteSpace(options.IntrospectionClientSecret))
        {
            failures.Add(
                "IntrospectionClientSecret is required when TokenMode accepts reference tokens.");
        }

        if (!acceptsReferenceTokens &&
            (!string.IsNullOrWhiteSpace(options.IntrospectionClientId) ||
             !string.IsNullOrWhiteSpace(options.IntrospectionClientSecret)))
        {
            failures.Add(
                "Introspection credentials cannot be configured when TokenMode is OnlyJwt.");
        }

        if (options.TokenMode == ModgudTokenMode.OnlyReferenceToken &&
            options.ConfigureJwtBearer is not null)
        {
            failures.Add(
                "ConfigureJwtBearer cannot be set when TokenMode is OnlyReferenceToken.");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                ModgudResourceServerDefaults.AuthenticationScheme,
                typeof(ModgudResourceServerOptions),
                failures);
        }
    }

    private static bool IsValidAuthority(string authority, bool requireHttps)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri)) return false;
        var isHttps = string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttp,
            StringComparison.OrdinalIgnoreCase);

        return (isHttps || isHttp) &&
               (!requireHttps || isHttps) &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/") &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    private sealed class ModgudResourceServerRegistrationMarker;
}

internal static class ModgudHttpClientNames
{
    public const string Introspection = "Modgud.ResourceServer.Introspection";
}
