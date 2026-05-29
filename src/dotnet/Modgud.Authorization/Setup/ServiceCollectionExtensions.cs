using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Resources;
using Modgud.Authorization.Services;

namespace Modgud.Authorization.Setup;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the authorization services. Expects the consumer to have already
    /// configured Marten — call <see cref="MartenStoreOptionsExtensions.UseModgudAuthorization"/>
    /// on the <see cref="StoreOptions"/> for the sub-class + event-alias wiring,
    /// and <see cref="MartenStoreOptionsExtensions.AddModgudAuthorizationPolymorphism"/>
    /// inside the STJ configure lambda.
    /// </summary>
    public static IServiceCollection AddModgudAuthorization(
        this IServiceCollection services,
        Action<AuthorizationOptions> configure)
    {
        var options = new AuthorizationOptions();
        configure(options);
        services.AddSingleton(options);

        services.AddSingleton<IResourceRegistry>(options.ResourceRegistry);

        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IEffectiveGroupsResolver, EffectiveGroupsResolver>();
        services.AddScoped<ILoginTimeMembershipDeriver, LoginTimeMembershipDeriver>();
        services.AddScoped<IMembershipEvaluator, MembershipEvaluator>();
        services.AddScoped<IPrincipalEmailResolver, PrincipalEmailResolver>();
        services.AddScoped<IPrincipalLookupService, PrincipalLookupService>();
        services.AddScoped<IAutoMembershipRecalculator, AutoMembershipRecalculator>();

        return services;
    }
}
