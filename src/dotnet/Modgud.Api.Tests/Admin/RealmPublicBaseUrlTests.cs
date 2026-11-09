using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Admin;

/// <summary>
/// F.routing-F-R7 — a realm's public origin is where its magic, reset and invite links
/// point and an accepted WebAuthn origin. Any absolute http(s) origin was accepted,
/// including a foreign host and userinfo; it must be on one of the realm's own domains.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RealmPublicBaseUrlTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://user:secret@{host}")]
    public async Task Create_refuses_an_origin_off_the_realms_domains(string origin)
    {
        var slug = $"pbu-{Guid.NewGuid():N}"[..14];
        var host = $"{slug}.localhost";

        var result = await Realms.CreateRealmAsync(new CreateRealmDto
        {
            Slug = slug, DisplayName = "Origin probe", Domains = [host],
            PublicBaseUrl = origin.Replace("{host}", host), IsActive = false,
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("Realm.", result.FirstError.Code);
    }

    [Fact]
    public async Task Update_refuses_a_foreign_origin_and_accepts_one_on_a_domain()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = $"pbu-{Guid.NewGuid():N}"[..14];
        var host = $"{slug}.localhost";
        var created = await Realms.CreateRealmAsync(new CreateRealmDto
        {
            Slug = slug, DisplayName = "Origin probe", Domains = [host], IsActive = false,
        }, ct);
        Assert.False(created.IsError, created.IsError ? created.FirstError.Description : "");

        var foreign = await Realms.UpdateRealmAsync(slug, new UpdateRealmDto { PublicBaseUrl = new("https://evil.example") }, ct);
        Assert.True(foreign.IsError);

        var own = await Realms.UpdateRealmAsync(slug, new UpdateRealmDto { PublicBaseUrl = new($"http://{host}:4300") }, ct);
        Assert.False(own.IsError, own.IsError ? own.FirstError.Description : "");
        Assert.Equal($"http://{host}:4300", own.Value.PublicBaseUrl);
    }

    private IRealmProvisioningService Realms => Factory.Services.GetRequiredService<IRealmProvisioningService>();
}
