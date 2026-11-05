using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// FM-01 — a manifest apply commits deactivations, grant revokes and deletions first and runs
/// their consequences (token, session and staffing revocation) afterwards. Those ran with the
/// request's token: an aborted request after the commit cancelled them, the configuration
/// change stayed persisted and access stayed valid — and a re-apply does not retry, because
/// the revocation is bound to the state transition that already happened. The consequences
/// are kill switches and now always run to completion.
/// </summary>
public sealed class ApplyDeferredConsequencesTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Deferred_consequences_run_to_completion_when_the_request_was_aborted()
    {
        var ct = TestContext.Current.CancellationToken;
        var ran = false;
        await using (var apply = await TenantApplyTransaction.BeginAsync(
                         Factory.Services.GetRequiredService<IDocumentStore>(), "system", ct))
        {
            using (apply.Activate())
            {
                apply.Defer("revoke", (_, token) =>
                {
                    // What every revoker does with a cancelled token.
                    token.ThrowIfCancellationRequested();
                    ran = true;
                    return Task.CompletedTask;
                });
                await apply.CommitAsync(ct);
            }

            using var aborted = new CancellationTokenSource();
            await aborted.CancelAsync();
            await apply.RunDeferredAsync(
                Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, aborted.Token);
        }

        Assert.True(ran, "a revocation deferred past the commit must not be cancelled by the request");
    }
}
