using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Positions;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Positions;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Api.Tests.Positions;

[Collection(IntegrationTestCollection.Name)]
public sealed class ActivationTokenTests : IntegrationTestBase
{
    public ActivationTokenTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Revoke_is_side_effect_free_while_the_feature_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Factory.Services.GetRequiredService<AppSettings>();
        settings.Features.PositionTerminals = true;
        var positionId = await CreatePositionAsync("token-feature-off", ct);
        var create = await Client.PostAsJsonAsync($"/api/position/{positionId}/activation-tokens",
            new { Label = "Feature-off key" }, JsonOptions, ct);
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync(ct));
        var token = (await create.Content.ReadFromJsonAsync<ActivationTokenDto>(JsonOptions, ct))!;

        try
        {
            settings.Features.PositionTerminals = false;
            var revoke = await Client.PostAsync($"/api/activation-token/{token.Id}/revoke", null, ct);
            Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

            using var scope = Factory.Services.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stored = await query.LoadAsync<ActivationToken>(new ShortGuid(token.Id).Guid, ct);
            Assert.Equal(ActivationTokenStatus.PendingRegistration, stored!.Status);
            Assert.Null(stored.RevokedAt);
            Assert.Null(stored.RevokedByUserId);
        }
        finally
        {
            settings.Features.PositionTerminals = true;
        }
    }

    [Fact]
    public async Task Logical_token_is_multi_position_rp_bound_and_irreversibly_revocable()
    {
        var ct = TestContext.Current.CancellationToken;
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = true;
        var first = await CreatePositionAsync("token-position-a", ct);
        var second = await CreatePositionAsync("token-position-b", ct);

        var create = await Client.PostAsJsonAsync($"/api/position/{first}/activation-tokens",
            new { Label = "Safe key 1" }, JsonOptions, ct);
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync(ct));
        var token = (await create.Content.ReadFromJsonAsync<ActivationTokenDto>(JsonOptions, ct))!;
        Assert.Equal(ActivationTokenStatus.PendingRegistration, token.Status);
        Assert.Equal([first], token.AssignedPositionIds);

        var assign = await Client.PostAsync(
            $"/api/position/{second}/activation-tokens/{token.Id}/assign", null, ct);
        Assert.True(assign.IsSuccessStatusCode, await assign.Content.ReadAsStringAsync(ct));
        var secondList = await Client.GetFromJsonAsync<List<ActivationTokenDto>>(
            $"/api/position/{second}/activation-tokens", JsonOptions, ct);
        Assert.Equal(token.Id, Assert.Single(secondList!).Id);

        // Registration normally writes this through the terminal-authenticated
        // FIDO endpoint. Seeding the resulting document keeps this lifecycle
        // test focused while pinning the separate, RP-bound credential model.
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ActivationTokenCredential
            {
                Id = Guid.CreateVersion7(),
                ActivationTokenId = new ShortGuid(token.Id).Guid,
                CredentialId = [1, 2, 3],
                PublicKey = [4, 5, 6],
                UserHandle = [7, 8, 9],
                RpId = "alerthub.localhost",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(ct);
        }

        var activate = await Client.PostAsync($"/api/activation-token/{token.Id}/reactivate", null, ct);
        Assert.True(activate.IsSuccessStatusCode, await activate.Content.ReadAsStringAsync(ct));
        token = (await activate.Content.ReadFromJsonAsync<ActivationTokenDto>(JsonOptions, ct))!;
        Assert.Equal(ActivationTokenStatus.Active, token.Status);
        Assert.Equal(["alerthub.localhost"], token.RegisteredRpIds);

        var disable = await Client.PostAsync($"/api/activation-token/{token.Id}/disable", null, ct);
        Assert.True(disable.IsSuccessStatusCode, await disable.Content.ReadAsStringAsync(ct));
        Assert.Equal(ActivationTokenStatus.Disabled,
            (await disable.Content.ReadFromJsonAsync<ActivationTokenDto>(JsonOptions, ct))!.Status);

        var unassign = await Client.DeleteAsync(
            $"/api/position/{first}/activation-tokens/{token.Id}", ct);
        Assert.True(unassign.IsSuccessStatusCode, await unassign.Content.ReadAsStringAsync(ct));
        token = (await unassign.Content.ReadFromJsonAsync<ActivationTokenDto>(JsonOptions, ct))!;
        Assert.DoesNotContain(first, token.AssignedPositionIds);
        Assert.Contains(second, token.AssignedPositionIds);

        var revoke = await Client.PostAsync($"/api/activation-token/{token.Id}/revoke", null, ct);
        Assert.True(revoke.IsSuccessStatusCode, await revoke.Content.ReadAsStringAsync(ct));
        Assert.Equal(ActivationTokenStatus.Revoked,
            (await revoke.Content.ReadFromJsonAsync<ActivationTokenDto>(JsonOptions, ct))!.Status);

        var resurrection = await Client.PostAsync($"/api/activation-token/{token.Id}/reactivate", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resurrection.StatusCode);
        Assert.Contains("ActivationToken.Revoked", await resurrection.Content.ReadAsStringAsync(ct));

        using var verifyScope = Factory.Services.CreateScope();
        var query = verifyScope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await query.LoadAsync<ActivationToken>(new ShortGuid(token.Id).Guid, ct);
        Assert.NotNull(stored!.RevokedAt);
        Assert.NotNull(stored.RevokedByUserId);
    }

    private async Task<string> CreatePositionAsync(string accountName, CancellationToken ct)
    {
        var response = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = accountName,
            TerminalPolicy = new
            {
                Enabled = true,
                AllowedActivationProofs = new[] { ActivationProofMethodIds.PositionToken },
            },
        }, JsonOptions, ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<PositionPrincipalDto>(JsonOptions, ct))!.Id;
    }
}
