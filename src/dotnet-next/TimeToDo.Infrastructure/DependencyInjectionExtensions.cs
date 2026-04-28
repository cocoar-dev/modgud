using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Application.Contracts;
using TimeToDo.Domain.DomainServices;
using TimeToDo.Domain.Repositories;
using TimeToDo.Infrastructure.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Repositories;
using TimeToDo.Infrastructure.QueryServices;

namespace TimeToDo.Infrastructure;

/// <summary>
/// Extension methods for registering Infrastructure services without Marten configuration.
/// Use this when Marten is already configured elsewhere (e.g., DataAccess layer).
/// </summary>
public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds Infrastructure services (repositories, query services, event dispatcher)
    /// without configuring Marten. Use this when Marten is already configured.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Register Domain Services
        services.AddScoped<TodoHierarchyService>();

        // Register Repository implementations
        services.AddScoped<ITodoRepository, MartenTodoRepository>();
        services.AddScoped<ICustomerRepository, MartenCustomerRepository>();
        services.AddScoped<IUserRepository, MartenUserRepository>();
        services.AddScoped<ICommentRepository, MartenCommentRepository>();

        // Register Query Services (read-side)
        services.AddScoped<ITodoQueryService, MartenTodoQueryService>();
        services.AddScoped<ICustomerQueryService, MartenCustomerQueryService>();
        services.AddScoped<IUserQueryService, MartenUserQueryService>();
        services.AddScoped<ICommentQueryService, MartenCommentQueryService>();

        // Register Event Dispatcher
        services.AddScoped<IEventDispatcher, SignalREventDispatcher>();

        return services;
    }
}
