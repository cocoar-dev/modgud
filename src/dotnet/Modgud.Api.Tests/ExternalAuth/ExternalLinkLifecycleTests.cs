using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Groups;
using Modgud.Api.Features.Shared;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.ExternalAuth;

/// <summary>
/// Lifecycle Phase 3 (Variant-C unlink mechanics) + Phase 4 (membership recompute
/// on link/unlink). Pins: self-service + admin unlink hard-delete and free the
/// (Issuer, Subject) slot; the last-auth-method guard; and that linking/unlinking
/// re-evaluates an auto-group keyed on <c>p.ExternalIdentities</c> (the confirmed
/// stale-membership gap), with the in-memory and Postgres-JSONB engines agreeing.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ExternalLinkLifecycleTests : IntegrationTestBase
{
    public ExternalLinkLifecycleTests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string Issuer = "https://idp.lifecycle.test/v2.0";

    // ── Phase 3 — unlink hard-delete + slot reuse ────────────────────────

    [Fact]
    public async Task SelfService_Unlink_HardDeletes_And_Frees_Slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Self", "Unlink", "su", "selfunlink@acme.com", password: "TestPass1234");
        var providerId = Guid.NewGuid();
        var linkId = await SeedLinkAsync(user.Id, providerId, "sub-su-1");
        var client = await CreateAuthenticatedClientAsync("su", "TestPass1234");

        var resp = await client.DeleteAsync($"/api/account/external-links/{new ShortGuid(linkId)}", ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Hard-deleted, not a soft tombstone.
        await using (var qs = GetTenantedSession())
            Assert.Null(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));

        // The (Issuer, Subject) slot is free — re-inserting the same pair succeeds
        // without a unique-index violation, and exactly one live link results.
        await SeedLinkAsync(user.Id, providerId, "sub-su-1");
        await using (var qs2 = GetTenantedSession())
        {
            var live = await qs2.Query<ExternalIdentityLink>().Where(l => l.Subject == "sub-su-1").CountAsync(ct);
            Assert.Equal(1, live);
        }
    }

    [Fact]
    public async Task Admin_ForceUnlink_HardDeletes_And_Removes_Person_Ref()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Force", "Unlink", "fu", "forceunlink@acme.com", password: "TestPass1234");
        var providerId = Guid.NewGuid();
        var linkId = await SeedLinkAsync(user.Id, providerId, "sub-fu-1");

        // The default Client is a realm admin (user:write via realm:admin bypass).
        var resp = await Client.DeleteAsync(
            $"/api/admin/users/{new ShortGuid(user.Id)}/external-links/{new ShortGuid(linkId)}", ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var qs = GetTenantedSession();
        Assert.Null(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));
        var person = await qs.LoadAsync<Person>(user.Id, ct);
        Assert.DoesNotContain(person!.ExternalIdentities, r => r.LinkId == linkId);
    }

    [Fact]
    public async Task Unlink_LastAuthMethod_IsBlocked()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Last", "Method", "lm", "lastmethod@acme.com", password: "TestPass1234");

        // Strip the password so the external link is the ONLY remaining factor.
        using (var scope = Factory.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var au = await um.FindByIdAsync(user.Id.ToString());
            await um.RemovePasswordAsync(au!);
        }
        var linkId = await SeedLinkAsync(user.Id, Guid.NewGuid(), "sub-lm-1");

        var resp = await Client.DeleteAsync(
            $"/api/admin/users/{new ShortGuid(user.Id)}/external-links/{new ShortGuid(linkId)}", ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("Idp.LastAuthMethod", body.GetProperty("Code").GetString());

        // Guard prevented deletion — the link is still there.
        await using var qs = GetTenantedSession();
        Assert.NotNull(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));
    }

    [Fact]
    public async Task SelfService_Unlink_OtherUsersLink_IsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = await Factory.CreateTestUserWithIdentityAsync("Aaa", "User", "au", "au@acme.com", password: "TestPass1234");
        var userB = await Factory.CreateTestUserWithIdentityAsync("Bbb", "User", "bu", "bu@acme.com", password: "TestPass1234");
        var linkId = await SeedLinkAsync(userB.Id, Guid.NewGuid(), "sub-b-1");
        var clientA = await CreateAuthenticatedClientAsync("au", "TestPass1234");

        var resp = await clientA.DeleteAsync($"/api/account/external-links/{new ShortGuid(linkId)}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        await using var qs = GetTenantedSession();
        Assert.NotNull(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));
    }

    [Fact]
    public async Task Admin_ForceUnlink_UserLinkMismatch_IsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await Factory.CreateTestUserWithIdentityAsync("Mis", "Match", "mm", "mismatch@acme.com", password: "TestPass1234");
        var linkId = await SeedLinkAsync(user.Id, Guid.NewGuid(), "sub-mm-1");

        // Pass a different (random) userId that does not own the link.
        var resp = await Client.DeleteAsync(
            $"/api/admin/users/{new ShortGuid(Guid.NewGuid())}/external-links/{new ShortGuid(linkId)}", ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        await using var qs = GetTenantedSession();
        Assert.NotNull(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));
    }

    [Fact]
    public async Task Admin_ForceUnlink_WithoutPermission_IsForbidden()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await Factory.CreateTestUserWithIdentityAsync("Tgt", "User", "tg", "target@acme.com", password: "TestPass1234");
        var linkId = await SeedLinkAsync(target.Id, Guid.NewGuid(), "sub-tg-1");
        // A non-admin user (no user:write).
        await Factory.CreateTestUserWithIdentityAsync("Non", "Admin", "na", "nonadmin@acme.com", password: "TestPass1234", isRealmAdmin: false);
        var nonAdmin = await CreateAuthenticatedClientAsync("na", "TestPass1234");

        var resp = await nonAdmin.DeleteAsync(
            $"/api/admin/users/{new ShortGuid(target.Id)}/external-links/{new ShortGuid(linkId)}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        await using var qs = GetTenantedSession();
        Assert.NotNull(await qs.LoadAsync<ExternalIdentityLink>(linkId, ct));
    }

    // ── Phase 4 — membership recompute on link/unlink ────────────────────

    [Fact]
    public async Task Link_Then_Unlink_Recomputes_ExternalIdentities_Auto_Group()
    {
        var groupId = await CreateAutoGroupAsync(
            $"(p) => Type.Is(p, 'person') && p.ExternalIdentities.some(x => x.Issuer === '{Issuer}')");
        var user = await Factory.CreateTestUserWithIdentityAsync(
            "Member", "Drift", "md", "memberdrift@acme.com", password: "TestPass1234");

        // No link yet → not a member.
        await RecalcPrincipalAsync(user.Id);
        Assert.DoesNotContain(user.Id, await GetMemberIdsAsync(groupId));

        // Link (mirror the user-stream event the processor emits) → recompute adds.
        var linkId = await SeedUserRefAsync(user.Id, Guid.NewGuid());
        await RecalcPrincipalAsync(user.Id);
        Assert.Contains(user.Id, await GetMemberIdsAsync(groupId));

        // Unlink (ref removed) → recompute drops the membership.
        await RemoveUserRefAsync(user.Id, linkId);
        await RecalcPrincipalAsync(user.Id);
        Assert.DoesNotContain(user.Id, await GetMemberIdsAsync(groupId));
    }

    [Fact]
    public void Phase4_Recompute_Handlers_Match_The_ReferenceSync_Registration_Pattern()
    {
        // Deterministic wiring guard for the exact failure mode the review flagged:
        // "if the handlers were accidentally not discovered, production membership
        // goes stale on link/unlink while every other test still passes."
        // ReferenceSyncRegistration.RegisterAll discovers handlers by scanning the
        // API assembly for concrete ReferenceSyncHandler<TEvent> subclasses and
        // routing each TEvent's forwarded IEvent<TEvent> to the reference-sync
        // queue. Assert both Phase-4 handlers match that exact predicate, so they
        // are auto-registered. (The async end-to-end *delivery* is not exercised:
        // the durable reference-sync queue does not drain inside
        // WebApplicationFactory — it is the same forwarding path the existing
        // AutoMembership handlers use in production.)
        AssertAutoRegisteredFor<AutoMembershipOnExternalIdentityLinkedHandler, UserExternalIdentityLinkedEvent>();
        AssertAutoRegisteredFor<AutoMembershipOnExternalIdentityUnlinkedHandler, UserExternalIdentityUnlinkedEvent>();
    }

    private static void AssertAutoRegisteredFor<THandler, TEvent>()
    {
        Assert.False(typeof(THandler).IsAbstract, $"{typeof(THandler).Name} must be concrete to be discovered");
        var baseType = typeof(THandler).BaseType;
        Assert.True(
            baseType is { IsGenericType: true }
            && baseType.GetGenericTypeDefinition() == typeof(ReferenceSyncHandler<>)
            && baseType.GetGenericArguments()[0] == typeof(TEvent),
            $"{typeof(THandler).Name} must derive from ReferenceSyncHandler<{typeof(TEvent).Name}> " +
            "so ReferenceSyncRegistration auto-routes its forwarded event");
    }

    [Fact]
    public async Task ExternalIdentities_Script_Agrees_Across_Both_Engines()
    {
        var groupId = await CreateAutoGroupAsync(
            $"(p) => Type.Is(p, 'person') && p.ExternalIdentities.some(x => x.Issuer === '{Issuer}')");

        var linked1 = await Factory.CreateTestUserWithIdentityAsync("Eng", "One", "e1", "e1@acme.com");
        var linked2 = await Factory.CreateTestUserWithIdentityAsync("Eng", "Two", "e2", "e2@acme.com");
        var unlinked = await Factory.CreateTestUserWithIdentityAsync("Eng", "Zero", "e0", "e0@acme.com");
        await SeedUserRefAsync(linked1.Id, Guid.NewGuid());
        await SeedUserRefAsync(linked2.Id, Guid.NewGuid());

        // Engine 1 — Postgres-JSONB full-group pass.
        await RecalcGroupAsync(groupId);
        var jsonb = await GetMemberIdsAsync(groupId);

        // Engine 2 — in-memory per-principal pass over every person, from empty.
        await ResetGroupMembersAsync(groupId);
        foreach (var personId in await AllPersonIdsAsync())
            await RecalcPrincipalAsync(personId);
        var inMemory = await GetMemberIdsAsync(groupId);

        Assert.Equal(jsonb.OrderBy(x => x), inMemory.OrderBy(x => x));
        Assert.Contains(linked1.Id, jsonb);
        Assert.Contains(linked2.Id, jsonb);
        Assert.DoesNotContain(unlinked.Id, jsonb);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<Guid> SeedLinkAsync(Guid userId, Guid providerId, string subject)
    {
        var linkId = Guid.NewGuid();
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.StartStream<ExternalIdentityLink>(linkId, new ExternalIdentityLinkedEvent(
            linkId, userId, providerId, Issuer, subject, null, null, DateTimeOffset.UtcNow));
        session.Events.Append(userId, new UserExternalIdentityLinkedEvent(
            userId, linkId, providerId, Issuer, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return linkId;
    }

    /// <summary>Seeds only the user-stream ref (drives Person.ExternalIdentities) — no link doc.</summary>
    private async Task<Guid> SeedUserRefAsync(Guid userId, Guid providerId)
    {
        var linkId = Guid.NewGuid();
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.Append(userId, new UserExternalIdentityLinkedEvent(
            userId, linkId, providerId, Issuer, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return linkId;
    }

    private async Task RemoveUserRefAsync(Guid userId, Guid linkId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.Append(userId, new UserExternalIdentityUnlinkedEvent(
            userId, linkId, Guid.NewGuid(), DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreateAutoGroupAsync(string script)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var evaluator = scope.ServiceProvider.GetRequiredService<IMembershipEvaluator>();
        var compiled = evaluator.TranspileMembershipScript(script);
        var id = Guid.CreateVersion7();
        session.Events.StartStream(id, new GroupCreatedEvent(
            id, $"Auto_{Guid.NewGuid():N}"[..12], null, [], [],
            MembershipMode.Auto, script, compiled, null, null, EmailMode.Shared,
            [AppSlugs.Modgud], ExternallyDrivable: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task RecalcPrincipalAsync(Guid principalId)
    {
        using var scope = Factory.Services.CreateScope();
        var recalc = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await recalc.RecalculateForPrincipalAsync(
            principalId, session,
            new[] { "Person.ExternalIdentities" }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task RecalcGroupAsync(Guid groupId)
    {
        using var scope = Factory.Services.CreateScope();
        var recalc = scope.ServiceProvider.GetRequiredService<IAutoMembershipRecalculator>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var group = await session.LoadAsync<Group>(groupId, TestContext.Current.CancellationToken);
        await recalc.RecalculateForGroupAsync(group!, session, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task ResetGroupMembersAsync(Guid groupId)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Events.Append(groupId, new GroupMembershipRecomputedEvent(groupId, []));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> GetMemberIdsAsync(Guid groupId)
    {
        await using var qs = GetTenantedSession();
        var group = await qs.LoadAsync<Group>(groupId, TestContext.Current.CancellationToken);
        return group!.MemberIds;
    }

    private async Task<IReadOnlyList<Guid>> AllPersonIdsAsync()
    {
        await using var qs = GetTenantedSession();
        return await qs.Query<Person>()
            .Where(p => !p.IsDeleted)
            .Select(p => p.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

}
