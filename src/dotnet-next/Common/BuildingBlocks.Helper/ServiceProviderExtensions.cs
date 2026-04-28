using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Helper;

public static class ServiceProviderExtensions
{
    /// <summary>
    /// Executes an asynchronous function within a new service scope, resolving one service.
    /// </summary>
    public static async Task<TResult> ExecuteInScopeAsync<TService, TResult>(
        this IServiceProvider serviceProvider,
        Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        using var scope = serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        return await action(service).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an asynchronous function within a new service scope, resolving two services.
    /// </summary>
    public static async Task<TResult> ExecuteInScopeAsync<TService1, TService2, TResult>(
        this IServiceProvider serviceProvider,
        Func<TService1, TService2, Task<TResult>> action)
        where TService1 : notnull
        where TService2 : notnull
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var service1 = sp.GetRequiredService<TService1>();
        var service2 = sp.GetRequiredService<TService2>();
        return await action(service1, service2).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an asynchronous function within a new service scope, resolving three services.
    /// </summary>
    public static async Task<TResult> ExecuteInScopeAsync<TService1, TService2, TService3, TResult>(
        this IServiceProvider serviceProvider,
        Func<TService1, TService2, TService3, Task<TResult>> action)
        where TService1 : notnull
        where TService2 : notnull
        where TService3 : notnull
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var service1 = sp.GetRequiredService<TService1>();
        var service2 = sp.GetRequiredService<TService2>();
        var service3 = sp.GetRequiredService<TService3>();
        return await action(service1, service2, service3).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an asynchronous function within a new service scope, resolving four services.
    /// </summary>
    public static async Task<TResult> ExecuteInScopeAsync<TService1, TService2, TService3, TService4, TResult>(
        this IServiceProvider serviceProvider,
        Func<TService1, TService2, TService3, TService4, Task<TResult>> action)
        where TService1 : notnull
        where TService2 : notnull
        where TService3 : notnull
        where TService4 : notnull
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;
        var service1 = sp.GetRequiredService<TService1>();
        var service2 = sp.GetRequiredService<TService2>();
        var service3 = sp.GetRequiredService<TService3>();
        var service4 = sp.GetRequiredService<TService4>();
        return await action(service1, service2, service3, service4).ConfigureAwait(false);
    }

    // Optional: Add synchronous versions if needed
    // Optional: Add overloads without TResult if the action doesn't return a value
}
