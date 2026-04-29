using Cocoar.Auth.Application.DTOs.LoginProviders;
using Cocoar.Auth.Application.Services;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authorization.AspNetCore;

namespace Cocoar.Auth.Api.Features.Admin.LoginProviders;

public static class LoginProvidersEndpoints
{
    public static WebApplication MapLoginProvidersEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/login-providers")
            .WithTags("Login Providers")
            .RequireAuthorization();

        group.MapGet("", async (LoginProviderService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAllAsync(ct)))
            .WithName("LoginProviders_List")
            .RequiresPermission("cocoar-auth:login-provider:read");

        group.MapGet("{id}", async (string id, LoginProviderService svc, CancellationToken ct) =>
        {
            var result = await svc.GetByIdAsync(id, ct);
            return result.ToResult(provider => Results.Ok(provider));
        })
        .WithName("LoginProviders_Get")
        .RequiresPermission("cocoar-auth:login-provider:read");

        group.MapPost("", async (CreateLoginProviderDto dto, LoginProviderService svc, CancellationToken ct) =>
        {
            var result = await svc.CreateAsync(dto, ct);
            return result.ToResult(provider => Results.Created($"{path}/admin/login-providers/{provider.Id}", provider));
        })
        .WithName("LoginProviders_Create")
        .RequiresPermission("cocoar-auth:login-provider:write");

        group.MapPatch("{id}", async (string id, UpdateLoginProviderDto dto, LoginProviderService svc, CancellationToken ct) =>
        {
            var result = await svc.UpdateAsync(id, dto, ct);
            return result.ToResult(provider => Results.Ok(provider));
        })
        .WithName("LoginProviders_Update")
        .RequiresPermission("cocoar-auth:login-provider:write");

        group.MapDelete("{id}", async (string id, LoginProviderService svc, CancellationToken ct) =>
        {
            var result = await svc.DeleteAsync(id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("LoginProviders_Delete")
        .RequiresPermission("cocoar-auth:login-provider:write");

        return app;
    }
}
