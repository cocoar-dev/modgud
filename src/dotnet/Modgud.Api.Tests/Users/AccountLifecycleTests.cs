using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Gdpr;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// Account-Lifecycle plan PR B (WS3 + WS4 + jobs): self-service grace deletion,
/// admin recycle-bin + restore, frozen edits while pending, and the scheduled
/// sweep that erases expired self-service requests / auto-purges the admin bin.
/// </summary>
public class AccountLifecycleTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Password = "TestPass1234";

    private async Task<(UserView user, HttpClient client)> CreateUserWithClientAsync(string acronym)
    {
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: acronym, lastname: "Life", acronym: acronym,
            email: $"{acronym.ToLowerInvariant()}@test.com", password: Password);
        var client = await CreateAuthenticatedClientAsync(acronym.ToLowerInvariant(), Password);
        return (user, client);
    }

    [Fact]
    public async Task SelfService_request_sets_grace_pending_then_cancel_clears()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client) = await CreateUserWithClientAsync("SLF");

        var request = await client.PostAsJsonAsync("/api/auth/delete-account",
            new { Password }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);

        var status = await client.GetFromJsonAsync<DeletionStatusDto>("/api/auth/deletion-status", JsonOptions, ct);
        Assert.True(status!.IsPending);
        Assert.Equal(DeletionInitiator.SelfService, status.Initiator);
        Assert.NotNull(status.ConfirmationDeadline);
        Assert.False(status.IsDataMasked);

        // The user stays active during grace — still authenticated, can cancel.
        var cancel = await client.PostAsync("/api/auth/cancel-deletion", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        var after = await client.GetFromJsonAsync<DeletionStatusDto>("/api/auth/deletion-status", JsonOptions, ct);
        Assert.False(after!.IsPending);
    }

    [Fact]
    public async Task AdminDelete_bins_user_then_restore_reactivates()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Bin", lastname: "Me", acronym: "BIN", email: "bin-me@test.com");
        var id = new ShortGuid(user.Id).ToString();

        var del = await Client.DeleteAsync($"/api/user/{id}", ct);
        del.EnsureSuccessStatusCode();
        // IsActive comes from UserView (async projection of UserDeactivatedEvent) —
        // wait for the daemon before asserting on it.
        await Factory.WaitForProjectionsAsync();

        var binned = await Client.GetFromJsonAsync<UserDto>($"/api/user/{id}", JsonOptions, ct);
        Assert.True(binned!.IsDeletionPending);
        Assert.Equal("Admin", binned.DeletionInitiator);
        Assert.False(binned.IsActive); // deactivated into the bin

        var restore = await Client.PostAsync($"/api/user/{id}/restore", null, ct);
        restore.EnsureSuccessStatusCode();
        await Factory.WaitForProjectionsAsync(); // UserActivatedEvent → UserView

        var restored = await Client.GetFromJsonAsync<UserDto>($"/api/user/{id}", JsonOptions, ct);
        Assert.False(restored!.IsDeletionPending);
        Assert.True(restored.IsActive);
    }

    [Fact]
    public async Task Edits_are_frozen_while_deletion_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Froz", lastname: "En", acronym: "FRZ", email: "frozen@test.com");
        var id = new ShortGuid(user.Id).ToString();

        (await Client.DeleteAsync($"/api/user/{id}", ct)).EnsureSuccessStatusCode();

        var update = await Client.PutAsJsonAsync($"/api/user/{id}",
            new { Firstname = "Renamed" }, JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
    }

    [Fact]
    public async Task SelfService_sweep_erases_grace_expired_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Exp", lastname: "Ired", acronym: "EXP", email: "expired@test.com");

        // Seed a self-service pending whose grace deadline is already in the past.
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new UserDeletionState
            {
                Id = user.Id,
                IsDeletionPending = true,
                DeletionInitiator = DeletionInitiator.SelfService,
                DeletionRequestedAt = DateTimeOffset.UtcNow.AddDays(-31),
                DeletionConfirmationDeadline = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await seed.SaveChangesAsync(ct);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var (_, erased) = await gdpr.RunSelfServiceSweepAsync(ct);
            Assert.Equal(1, erased);
        }

        await using var read = GetTenantedSession();
        var state = await read.LoadAsync<UserDeletionState>(user.Id, ct);
        Assert.True(state!.IsDataMasked);
        Assert.False(state.IsDeletionPending);
        var appUser = await read.LoadAsync<ApplicationUser>(user.Id, ct);
        Assert.True(appUser!.IsDeleted);
        Assert.Null(appUser.NormalizedEmail); // email released
    }

    [Fact]
    public async Task Admin_auto_purge_erases_only_past_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        var pastUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Past", lastname: "Ret", acronym: "PRT", email: "past-ret@test.com");
        var futureUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Fut", lastname: "Ret", acronym: "FRT", email: "future-ret@test.com");

        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new UserDeletionState
            {
                Id = pastUser.Id,
                IsDeletionPending = true,
                DeletionInitiator = DeletionInitiator.Admin,
                DeletionConfirmationDeadline = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            seed.Store(new UserDeletionState
            {
                Id = futureUser.Id,
                IsDeletionPending = true,
                DeletionInitiator = DeletionInitiator.Admin,
                DeletionConfirmationDeadline = DateTimeOffset.UtcNow.AddDays(10),
            });
            await seed.SaveChangesAsync(ct);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var gdpr = scope.ServiceProvider.GetRequiredService<IGdprService>();
            var purged = await gdpr.RunAdminRetentionPurgeAsync(ct);
            Assert.Equal(1, purged); // only the past-retention user (AutoPurge defaults on)
        }

        await using var read = GetTenantedSession();
        Assert.True((await read.LoadAsync<UserDeletionState>(pastUser.Id, ct))!.IsDataMasked);
        var future = await read.LoadAsync<UserDeletionState>(futureUser.Id, ct);
        Assert.False(future!.IsDataMasked);
        Assert.True(future.IsDeletionPending);
    }
}
