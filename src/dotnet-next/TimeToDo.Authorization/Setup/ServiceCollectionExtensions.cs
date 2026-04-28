using Marten;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Authorization.Access;
using TimeToDo.Authorization.Membership;
using TimeToDo.Authorization.Resources;
using TimeToDo.Authorization.Services;

namespace TimeToDo.Authorization.Setup;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the authorization services. Expects the consumer to have already
    /// configured Marten — call <see cref="MartenStoreOptionsExtensions.UseTimeTodoAuthorization"/>
    /// on the <see cref="StoreOptions"/> for the sub-class + event-alias wiring,
    /// and <see cref="MartenStoreOptionsExtensions.AddTimeTodoAuthorizationPolymorphism"/>
    /// inside the STJ configure lambda.
    /// </summary>
    public static IServiceCollection AddTimeTodoAuthorization(
        this IServiceCollection services,
        Action<AuthorizationOptions> configure)
    {
        var options = new AuthorizationOptions();
        configure(options);
        services.AddSingleton(options);

        services.AddSingleton<IResourceRegistry>(options.ResourceRegistry);

        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IAccessPolicyEngine, AccessPolicyEngine>();
        services.AddScoped<IMembershipEvaluator, MembershipEvaluator>();
        services.AddScoped<IPrincipalEmailResolver, PrincipalEmailResolver>();
        services.AddScoped<IPrincipalLookupService, PrincipalLookupService>();
        services.AddScoped<IAutoMembershipRecalculator, AutoMembershipRecalculator>();

        return services;
    }
}
