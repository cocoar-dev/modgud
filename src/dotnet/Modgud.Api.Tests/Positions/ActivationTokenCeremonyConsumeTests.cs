using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Positions;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Api.Tests.Positions;

/// <summary>
/// G-B4-05 — a registration ceremony is single use. Two finishes that both loaded it
/// before either consumed it must not both pass: Marten does not version-check a
/// delete, so the consume is a version-checked store of its consume marker.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ActivationTokenCeremonyConsumeTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Only_one_of_two_concurrent_finishes_consumes_the_ceremony()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        using (var seed = TenantScope())
        {
            var session = seed.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ActivationTokenRegistrationCeremony
            {
                Id = id,
                ActivationTokenId = Guid.NewGuid(),
                TerminalEnrollmentId = Guid.NewGuid(),
                ClientId = "terminal",
                RpId = "localhost",
                OptionsJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            });
            await session.SaveChangesAsync(ct);
        }

        using var first = TenantScope();
        using var second = TenantScope();
        var a = first.ServiceProvider.GetRequiredService<IDocumentSession>();
        var b = second.ServiceProvider.GetRequiredService<IDocumentSession>();
        var loadedByA = await a.LoadAsync<ActivationTokenRegistrationCeremony>(id, ct);
        var loadedByB = await b.LoadAsync<ActivationTokenRegistrationCeremony>(id, ct);

        Assert.True(await ActivationTokenEndpoints.TryConsumeCeremonyAsync(a, loadedByA!, ct));
        Assert.False(await ActivationTokenEndpoints.TryConsumeCeremonyAsync(b, loadedByB!, ct));
    }

    private IServiceScope TenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
