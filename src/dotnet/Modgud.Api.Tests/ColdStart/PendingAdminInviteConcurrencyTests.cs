using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// IS-INV-01 — "at most one open admin invite" and "a revoked invite is not
/// consumed" must hold under concurrency. The service read the open invites and
/// wrote without a lock or version check, so two issuers could each leave one open,
/// and a consume that loaded an invite could still complete after a reissue revoked
/// it. Both are pinned on the documents the service writes.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class PendingAdminInviteConcurrencyTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Two_issuers_cannot_both_leave_an_open_invite()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = TenantScope();
        using var second = TenantScope();
        var a = first.ServiceProvider.GetRequiredService<IDocumentSession>();
        var b = second.ServiceProvider.GetRequiredService<IDocumentSession>();

        // Both read "no open invite" before either commits.
        Assert.Empty(await a.Query<PendingAdminInvite>().Where(i => i.OpenSlot != null).ToListAsync(ct));
        Assert.Empty(await b.Query<PendingAdminInvite>().Where(i => i.OpenSlot != null).ToListAsync(ct));

        a.Store(Invite("first"));
        await a.SaveChangesAsync(ct);
        b.Store(Invite("second"));
        // Marten reports the open-slot unique violation (23505) as DocumentAlreadyExists.
        await Assert.ThrowsAsync<JasperFx.DocumentAlreadyExistsException>(() => b.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task A_consume_cannot_claim_an_invite_a_reissue_revoked_after_it_was_loaded()
    {
        var ct = TestContext.Current.CancellationToken;
        var invite = Invite("claimed");
        using (var seed = TenantScope())
        {
            var session = seed.ServiceProvider.GetRequiredService<IDocumentSession>();
            // A used invite does not hold the open slot; keep this test independent of the other.
            session.DeleteWhere<PendingAdminInvite>(i => i.OpenSlot != null);
            session.Store(invite);
            await session.SaveChangesAsync(ct);
        }

        using var consumeScope = TenantScope();
        using var reissueScope = TenantScope();
        var consume = consumeScope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var reissue = reissueScope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var loadedByConsume = await consume.LoadAsync<PendingAdminInvite>(invite.Id, ct);
        var loadedByReissue = await reissue.LoadAsync<PendingAdminInvite>(invite.Id, ct);

        loadedByReissue!.UsedAt = DateTimeOffset.UtcNow;
        loadedByReissue.OpenSlot = null;
        reissue.Store(loadedByReissue);
        await reissue.SaveChangesAsync(ct);

        loadedByConsume!.UsedAt = DateTimeOffset.UtcNow;
        loadedByConsume.OpenSlot = null;
        consume.Store(loadedByConsume);
        await Assert.ThrowsAsync<JasperFx.ConcurrencyException>(() => consume.SaveChangesAsync(ct));
    }

    private static PendingAdminInvite Invite(string tag) => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"inv-{tag}",
        Email = $"inv-{tag}@invite.example",
        TokenHash = Guid.NewGuid().ToString("N"),
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        CreatedAt = DateTimeOffset.UtcNow,
        OpenSlot = PendingAdminInvite.OpenSlotValue,
    };

    private IServiceScope TenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
