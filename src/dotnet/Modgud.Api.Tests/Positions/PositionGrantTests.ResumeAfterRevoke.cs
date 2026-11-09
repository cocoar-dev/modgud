using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Api.Tests.Positions;

public partial class PositionGrantTests
{
    /// <summary>
    /// B3-P1 — a resume that read the grant before an overlapping revoke committed could
    /// append PositionGrantResumed to the revoked stream, and the projection set the grant
    /// back to Active. Revoked is terminal: a late resume or suspend must not revive it.
    /// The append below is exactly what the lost race leaves in the stream.
    /// </summary>
    [Fact]
    public async Task A_resume_that_lost_the_race_against_a_revoke_does_not_revive_the_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var fn = await CreatePositionAsync("fn-late-resume", ct);
        var user = await CreateUserAsync("lr", ct);
        var grant = await IssueAsync(fn, user, ct);
        await PostTransitionAsync(fn, grant, "suspend", ct);
        await PostTransitionAsync(fn, grant, "revoke", ct);

        var grantId = new ShortGuid(grant).Guid;
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(grantId, new PositionGrantResumed(grantId, Guid.NewGuid(), DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(ct);
        }

        using var read = Factory.Services.CreateScope();
        var doc = await read.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<PositionGrant>(grantId, ct);
        Assert.NotNull(doc);
        Assert.Equal(PositionGrantStatus.Revoked, doc.Status);
    }
}
