using Cocoar.Auth.Domain.OAuth.Applications;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// OpenIddict's default <see cref="OpenIddictApplicationManager{TApplication}"/>
/// hashes/validates client secrets using ASP.NET Core's PBKDF2 password hasher.
/// Cocoar.Auth stores OAuth client secrets as BCrypt hashes (see
/// <c>OAuthAdminMapping.HashSecret</c>), so the default validator rejects every
/// otherwise-valid secret with <c>invalid_client</c>.
///
/// <para>This subclass overrides only the two methods that touch the secret
/// hash format — obfuscation (write path) and validation (read path) — and
/// delegates everything else to the base manager. Behaviour the rest of the
/// codebase already depends on (manual <c>HashSecret</c> at create-client
/// time, <c>VerifySecret</c> for API secrets) keeps using BCrypt directly,
/// so the storage format is consistent across the whole admin surface.</para>
/// </summary>
public sealed class CocoarOpenIddictApplicationManager : OpenIddictApplicationManager<OAuthApplicationState>
{
    public CocoarOpenIddictApplicationManager(
        IOpenIddictApplicationCache<OAuthApplicationState> cache,
        ILogger<OpenIddictApplicationManager<OAuthApplicationState>> logger,
        IOptionsMonitor<OpenIddictCoreOptions> options,
        IOpenIddictApplicationStore<OAuthApplicationState> store)
        : base(cache, logger, options, store)
    {
    }

    /// <summary>
    /// Returns the BCrypt hash of <paramref name="secret"/>. The seed
    /// (<c>OAuthAdminMapping.HashSecret</c>) and any subsequent rotation
    /// path through the admin API both use BCrypt — keeping this manager
    /// in lockstep means OpenIddict's create-client flow produces the same
    /// hash format whether the client is created via REST or seeded.
    /// </summary>
    protected override ValueTask<string> ObfuscateClientSecretAsync(
        string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return ValueTask.FromResult(BCrypt.Net.BCrypt.HashPassword(secret, workFactor: 12));
    }

    /// <summary>
    /// Validates a presented secret against the BCrypt hash returned by
    /// <see cref="OpenIddictApplicationManager{TApplication}.GetClientSecretAsync(TApplication, CancellationToken)"/>.
    /// Returns false on any malformed-hash exception so a corrupted record
    /// rejects the request with <c>invalid_client</c> rather than 500.
    /// </summary>
    public override async ValueTask<bool> ValidateClientSecretAsync(
        OAuthApplicationState application, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var hash = await Store.GetClientSecretAsync(application, cancellationToken);
        if (string.IsNullOrEmpty(hash)) return false;

        try { return BCrypt.Net.BCrypt.Verify(secret, hash); }
        catch { return false; }
    }
}
