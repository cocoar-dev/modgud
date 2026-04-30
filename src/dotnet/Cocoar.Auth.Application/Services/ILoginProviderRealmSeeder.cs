using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Application.Services;

/// <summary>
/// Seeds the built-in <c>Internal</c> login provider into a tenant database.
/// The implementation lives in the Authentication slice (where the
/// LoginProvider aggregate is defined); the interface lives here so the
/// Infrastructure-layer realm provisioning service can invoke it without
/// taking a project reference on Authentication.
/// </summary>
public interface ILoginProviderRealmSeeder
{
    Task SeedAsync(string tenantId, ILogger? logger = null, CancellationToken ct = default);
}
