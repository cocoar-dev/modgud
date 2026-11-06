using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Authentication.Registration;
using Modgud.Domain.Users.Events;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// AREG-05 — the account and the rest of the registration (default groups, the registration
/// event, the hard delete of the pending record) were two commits. A failure between them left
/// an account with a consumed pending record but no registration event or groups, and a retry
/// could not repair it. They are one commit now: a failure leaves nothing, and the same proof
/// can be repeated.
/// </summary>
public partial class RegistrationPipelineTests
{
    [Fact]
    public async Task A_failing_registration_commit_leaves_no_account_and_the_proof_can_be_repeated()
    {
        var ct = TestContext.Current.CancellationToken;
        const string host = "rp-partial.localhost";
        const string email = "rp-partial@example.test";
        await SetUpJitAppAsync("rp-partial", host);
        var code = ExtractCode(await RequestCodeAsync(host, email));

        using (var scope = CreateTenantScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Listeners.Add(new FailTheRegistrationCommit());
            var pipeline = scope.ServiceProvider.GetRequiredService<IRegistrationPipeline>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ProveCodeAsync(email, code, ct));
        }

        Assert.Null(await QueryUserByEmailAsync(email));
        var pending = await LoadPendingAsync(email);
        Assert.NotNull(pending);
        Assert.Null(pending!.ConsumedAt);

        using (var scope = CreateTenantScope())
        {
            var pipeline = scope.ServiceProvider.GetRequiredService<IRegistrationPipeline>();
            var retried = await pipeline.ProveCodeAsync(email, code, ct);
            Assert.False(retried.IsError, retried.IsError ? retried.FirstError.Code : null);
        }

        var user = Assert.Single(await QueryUsersByEmailAsync(email));
        Assert.Null(await LoadPendingAsync(email));
        using (var scope = CreateTenantScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var events = await session.Events.FetchStreamAsync(user.Id, token: ct);
            Assert.Single(events, e => e.Data is UserRegisteredEvent);
        }
    }

    /// <summary>Fails the commit that carries the registration event.</summary>
    private sealed class FailTheRegistrationCommit : DocumentSessionListenerBase
    {
        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (session.PendingChanges.Streams().SelectMany(s => s.Events).Any(e => e.Data is UserRegisteredEvent))
                throw new InvalidOperationException("Injected failure of the registration commit.");
            return Task.CompletedTask;
        }
    }
}
