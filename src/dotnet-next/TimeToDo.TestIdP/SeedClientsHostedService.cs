using OpenIddict.Abstractions;
using TimeToDo.TestIdP.Config;

namespace TimeToDo.TestIdP;

/// <summary>
/// Rehydrates OpenIddict's application store from the JSON config on startup.
/// We accept any redirect URI that *starts with* one of the configured prefixes
/// (registered via <c>PostLogoutRedirectUris</c> too, loosely). Since TimeToDo's
/// callback path embeds a GUID, allowing a prefix match per host is enough.
/// </summary>
public class SeedClientsHostedService(
    IServiceProvider services,
    TestIdpConfig config,
    ILogger<SeedClientsHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestIdpDbContext>();
        await dbContext.Database.EnsureCreatedAsync(ct);

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        foreach (var client in config.Clients)
        {
            var existing = await manager.FindByClientIdAsync(client.ClientId, ct);
            if (existing is not null)
            {
                await manager.DeleteAsync(existing, ct);
            }

            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = client.ClientId,
                ClientSecret = client.ClientSecret,
                ClientType = OpenIddictConstants.ClientTypes.Confidential,
                DisplayName = $"TestIdP:{client.ClientId}",
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.EndSession,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    OpenIddictConstants.Permissions.Scopes.Email,
                    OpenIddictConstants.Permissions.Scopes.Profile,
                    OpenIddictConstants.Permissions.Prefixes.Scope + "groups",
                    OpenIddictConstants.Permissions.Prefixes.Scope + "roles",
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
                }
            };

            foreach (var uri in client.RedirectUris)
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out var u))
                    descriptor.RedirectUris.Add(u);
            }

            await manager.CreateAsync(descriptor, ct);
            logger.LogInformation(
                "[TestIdP] Registered client '{ClientId}' with {Count} redirect prefix(es)",
                client.ClientId, descriptor.RedirectUris.Count);
        }

        logger.LogInformation("[TestIdP] Users available: {Users}",
            string.Join(", ", config.Users.Select(u => u.UserName)));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
