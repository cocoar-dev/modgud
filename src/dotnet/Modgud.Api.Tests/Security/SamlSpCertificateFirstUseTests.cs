using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.Saml;
using Modgud.Authentication.Identity.LoginProviders.Saml;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B6-SAML-06 — two first uses of a realm's SAML SP certificate (metadata, a signed
/// AuthnRequest) could each generate a key pair and Store it, last write wins. The loser
/// had already handed out its own certificate, which the realm then no longer holds:
/// a published SP certificate or a signed request the realm cannot back. A test-side
/// Marten listener holds both first saves so they really overlap.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SamlSpCertificateFirstUseTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Overlapping_first_uses_hand_out_the_one_stored_certificate()
    {
        var options = Assert.IsType<StoreOptions>(Factory.Services.GetRequiredService<IDocumentStore>().Options);
        var hold = new Hold(session => session.PendingChanges.AllChangedFor<SamlSpCertificateDocument>().Any(), arrivals: 2);
        options.Listeners.Add(hold);
        try
        {
            var results = await Task.WhenAll(FirstUseAsync(), FirstUseAsync()).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(2, hold.Arrivals);

            await using var query = GetTenantedSession();
            var stored = await query.LoadAsync<SamlSpCertificateDocument>(SamlSpCertificateDocument.SingletonId, Ct);
            Assert.NotNull(stored);
            Assert.All(results, thumbprint => Assert.Equal(stored!.ActiveCertThumbprint, thumbprint));
        }
        finally
        {
            hold.Release();
            options.Listeners.Remove(hold);
        }
    }

    private async Task<string> FirstUseAsync()
    {
        await Task.Yield();
        using var tenant = TenantContext.Enter(TenantConstants.SystemTenantId);
        using var scope = Factory.Services.CreateScope();
        var cert = await scope.ServiceProvider.GetRequiredService<SamlSpCertificateService>().GetActiveAsync(Ct);
        return cert.Thumbprint;
    }

    private sealed class Hold(Func<IDocumentSession, bool> matches, int arrivals) : DocumentSessionListenerBase
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (_open.Task.IsCompleted || !matches(session)) return;
            if (Interlocked.Increment(ref _arrivals) >= arrivals) _open.TrySetResult();
            await _open.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }

        public void Release() => _open.TrySetResult();
    }
}
