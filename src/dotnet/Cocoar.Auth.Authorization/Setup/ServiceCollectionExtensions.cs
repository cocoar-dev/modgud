using Marten;
using Microsoft.Extensions.DependencyInjection;
using Cocoar.Auth.Authorization.Membership;
using Cocoar.Auth.Authorization.Resources;
using Cocoar.Auth.Authorization.Services;

namespace Cocoar.Auth.Authorization.Setup;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the authorization services. Expects the consumer to have already
    /// configured Marten — call <see cref="MartenStoreOptionsExtensions.UseCocoarAuthAuthorization"/>
    /// on the <see cref="StoreOptions"/> for the sub-class + event-alias wiring,
    /// and <see cref="MartenStoreOptionsExtensions.AddCocoarAuthAuthorizationPolymorphism"/>
    /// inside the STJ configure lambda.
    /// </summary>
    public static IServiceCollection AddCocoarAuthAuthorization(
        this IServiceCollection services,
        Action<AuthorizationOptions> configure)
    {
        var options = new AuthorizationOptions();
        configure(options);
        services.AddSingleton(options);

        services.AddSingleton<IResourceRegistry>(options.ResourceRegistry);

        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IEffectiveGroupsResolver, EffectiveGroupsResolver>();
        services.AddScoped<IMembershipEvaluator, MembershipEvaluator>();
        services.AddScoped<IPrincipalEmailResolver, PrincipalEmailResolver>();
        services.AddScoped<IPrincipalLookupService, PrincipalLookupService>();
        services.AddScoped<IAutoMembershipRecalculator, AutoMembershipRecalculator>();

        return services;
    }
}
