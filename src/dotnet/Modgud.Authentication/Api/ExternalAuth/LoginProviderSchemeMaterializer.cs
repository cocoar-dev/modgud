using System.Collections.Concurrent;
using Marten;
using Modgud.Authentication.Api.ExternalAuth.Saml;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// Keeps this node's registered OIDC schemes and SAML provider cache equal to
/// what the realm's <see cref="LoginProvider"/> documents say — from the
/// database, on demand, on every node (ADR 0010, D6).
/// <para>
/// Before this class, a scheme was registered by the Wolverine handler that ran
/// on the node which saved the provider. A second node never heard about it and
/// threw on the first challenge. Now every node resolves its own view: the
/// request path calls <see cref="EnsureFreshAsync"/> (a timestamp check, and at
/// most one small query per realm per <see cref="RevalidateInterval"/>), the
/// saving node's handlers call <see cref="RefreshAsync"/> so their own view is
/// exact immediately, and the cold-start bootstrap warms every active realm.
/// The same bounded-staleness contract as <c>RealmKeyStore</c>: another node's
/// change is visible here within seconds, never "after a restart".
/// </para>
/// <para>
/// Change detection uses the document's <c>UpdatedAt</c> stamp, which every
/// mutation event bumps. Registration of a SAML provider fetches IdP metadata,
/// so an unchanged provider is deliberately never re-registered.
/// </para>
/// </summary>
public sealed class LoginProviderSchemeMaterializer(
    IDocumentStore store,
    DynamicOidcSchemeManager oidc,
    DynamicSamlSchemeManager saml,
    TimeProvider clock,
    ILogger<LoginProviderSchemeMaterializer> logger)
{
    /// <summary>
    /// Upper bound on how long another node's login-provider change stays
    /// invisible here. A login-provider edit is a rare admin action; a query
    /// this small every 15 s per realm costs nothing measurable.
    /// </summary>
    public static readonly TimeSpan RevalidateInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, RealmState> _realms = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes sure the realm's providers were checked against the database within
    /// <see cref="RevalidateInterval"/>. Cheap when fresh; safe to call on every request.
    /// A null or empty realm (no tenant resolved) is a no-op.
    /// </summary>
    public ValueTask EnsureFreshAsync(string? realmSlug, CancellationToken ct = default)
        => string.IsNullOrEmpty(realmSlug) ? ValueTask.CompletedTask : SyncAsync(realmSlug, force: false, ct);

    /// <summary>
    /// Re-reads the realm's providers now, regardless of the interval. Called by
    /// the node that just committed a change so its own view is exact at once.
    /// </summary>
    public ValueTask RefreshAsync(string realmSlug, CancellationToken ct = default)
        => SyncAsync(realmSlug, force: true, ct);

    /// <summary>Drops everything registered for a realm that no longer exists.</summary>
    public async Task ForgetAsync(string realmSlug)
    {
        if (!_realms.TryRemove(realmSlug, out var state)) return;
        await state.Gate.WaitAsync();
        try
        {
            foreach (var (id, fingerprint) in state.Registered)
                await UnregisterAsync(id, fingerprint.Type);
            state.Registered.Clear();
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async ValueTask SyncAsync(string realmSlug, bool force, CancellationToken ct)
    {
        var state = _realms.GetOrAdd(realmSlug, _ => new RealmState());
        if (!force && IsFresh(state)) return;

        await state.Gate.WaitAsync(ct);
        try
        {
            // A peer request may have refreshed while we waited for the gate.
            if (!force && IsFresh(state)) return;

            IReadOnlyList<LoginProvider> current;
            await using (var session = store.QuerySession(realmSlug))
            {
                current = await session.Query<LoginProvider>()
                    .Where(p => !p.IsDeleted && p.Enabled
                        && (p.Type == LoginProviderType.Oidc || p.Type == LoginProviderType.Saml))
                    .ToListAsync(ct);
            }

            // The managers stamp each registration with the ambient realm.
            using var _ = TenantContext.Enter(realmSlug);

            var seen = new HashSet<Guid>();
            foreach (var provider in current)
            {
                seen.Add(provider.Id);
                var fingerprint = new Fingerprint(provider.Type, provider.UpdatedAt);
                if (state.Registered.TryGetValue(provider.Id, out var known) && known == fingerprint)
                    continue;

                try
                {
                    if (known is not null && known.Type != provider.Type)
                        await UnregisterAsync(provider.Id, known.Type);

                    await RegisterAsync(provider);
                    state.Registered[provider.Id] = fingerprint;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Leave it unregistered and try again on the next pass; one
                    // broken provider must not block the others.
                    logger.LogError(ex,
                        "Login provider {Id} ({Type}) in realm {Realm} could not be registered on this node",
                        provider.Id, provider.Type, realmSlug);
                }
            }

            foreach (var gone in state.Registered.Where(kv => !seen.Contains(kv.Key)).ToList())
            {
                await UnregisterAsync(gone.Key, gone.Value.Type);
                state.Registered.Remove(gone.Key);
            }

            state.CheckedAt = clock.GetUtcNow();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed read keeps the previous view; the next request retries.
            logger.LogWarning(ex, "Could not read login providers of realm {Realm}; keeping the last known set", realmSlug);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private bool IsFresh(RealmState state) => clock.GetUtcNow() - state.CheckedAt < RevalidateInterval;

    private Task RegisterAsync(LoginProvider provider) => provider.Type switch
    {
        LoginProviderType.Oidc => oidc.RegisterAsync(provider),
        LoginProviderType.Saml => saml.RegisterAsync(provider),
        _ => Task.CompletedTask,
    };

    private Task UnregisterAsync(Guid id, LoginProviderType type) => type switch
    {
        LoginProviderType.Oidc => oidc.UnregisterAsync(id),
        LoginProviderType.Saml => saml.UnregisterAsync(id),
        _ => Task.CompletedTask,
    };

    private sealed record Fingerprint(LoginProviderType Type, DateTimeOffset UpdatedAt);

    private sealed class RealmState
    {
        public DateTimeOffset CheckedAt = DateTimeOffset.MinValue;
        public readonly Dictionary<Guid, Fingerprint> Registered = new();
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
}
