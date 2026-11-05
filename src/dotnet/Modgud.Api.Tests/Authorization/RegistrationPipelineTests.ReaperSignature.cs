using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Jobs;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// AREG-08 — the reaper's signature did not prove the account came from the old
/// passwordless sign-up: a missing OTP challenge satisfied "no consumed challenge", so an
/// old, unconfirmed, passwordless account an admin created or imported (no code ever issued)
/// was erased too. A ghost is now only an account whose sign-up code was issued and never
/// redeemed.
/// </summary>
public partial class RegistrationPipelineTests
{
    [Fact]
    public async Task Reaper_spares_an_old_admin_created_passwordless_account_without_a_code()
    {
        var ct = TestContext.Current.CancellationToken;
        const string adminMadeEmail = "rp-admin-made@example.test";
        Guid adminMadeId;
        using (var scope = CreateTenantScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            // Unconfirmed, passwordless, old — but no sign-up code was ever issued.
            var adminMade = new ApplicationUser(adminMadeEmail, adminMadeEmail) { IsActive = true };
            Assert.True((await userManager.CreateAsync(adminMade)).Succeeded);
            adminMadeId = adminMade.Id;
            await BackdateStreamAsync(session, adminMade.Id, TimeSpan.FromDays(30), ct);
        }

        using (var scope = CreateTenantScope())
        {
            var job = ActivatorUtilities.CreateInstance<UnconfirmedRegistrationReaperJob>(scope.ServiceProvider);
            await job.RunAsync(dryRun: false, olderThanDays: 7, ct);
        }

        using (var scope = CreateTenantScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var kept = await session.LoadAsync<ApplicationUser>(adminMadeId, ct);
            Assert.NotNull(kept);
            Assert.False(kept!.IsDeleted, "an account without an unredeemed sign-up code is no legacy ghost");
        }
    }
}
