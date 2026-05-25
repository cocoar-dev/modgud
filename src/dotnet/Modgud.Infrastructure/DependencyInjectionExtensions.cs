using Microsoft.Extensions.DependencyInjection;
using Modgud.Application.Contracts;
using Modgud.Infrastructure.Events;

namespace Modgud.Infrastructure;

/// <summary>
/// Extension methods for registering Infrastructure services without Marten configuration.
/// Use this when Marten is already configured elsewhere (e.g., DataAccess layer).
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds Infrastructure services (event dispatcher) without configuring Marten.
    /// Use this when Marten is already configured.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Register Event Dispatcher
        services.AddScoped<IEventDispatcher, SignalREventDispatcher>();

        return services;
    }
}
