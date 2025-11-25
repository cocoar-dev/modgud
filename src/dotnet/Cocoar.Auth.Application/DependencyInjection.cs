using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<UserService>();
        services.AddScoped<RoleService>();
        services.AddScoped<AuthService>();

        return services;
    }
}
