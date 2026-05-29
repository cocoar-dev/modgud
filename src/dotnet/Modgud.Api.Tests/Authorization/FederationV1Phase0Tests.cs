using BuildingBlocks.Helper;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;
using Modgud.Authorization.Commands;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using ErrorOr;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Federation v1 — Phase 0 (flags, store, config guard). Pins the three new
/// flags persist + project + round-trip, the realm:admin-local-only config guard
/// (decision G), and the fail-closed default. No login-path behavior is wired yet
/// (Phases 1–4); these tests cover only the additive config/store surface.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FederationV1Phase0Tests : IntegrationTestBase
{
    public FederationV1Phase0Tests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CreateGroup_ExternallyDrivable_With_RealmAdminRole_Is_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var adminRole = await Factory.CreateTestRoleAsync(
            $"RealmAdmin_{Guid.NewGuid():N}", isRealmAdmin: true);

        using var scope = Factory.Services.CreateScope();
        var handler = NewCreateGroupHandler(scope.ServiceProvider);

        var result = await handler.Handle(new CreateGroupCommand(
            Name: $"Drivable_{Guid.NewGuid():N}", Description: null,
            MemberIds: [], RoleIds: [adminRole.Id],
            ExternallyDrivable: true), ct);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "Group.ExternallyDrivableRealmAdmin");
    }

    [Fact]
    public async Task CreateGroup_ExternallyDrivable_With_NormalRole_Succeeds_And_Projects()
    {
        var ct = TestContext.Current.CancellationToken;
        var role = await Factory.CreateTestRoleAsync($"Normal_{Guid.NewGuid():N}");

        using var scope = Factory.Services.CreateScope();
        var handler = NewCreateGroupHandler(scope.ServiceProvider);

        var result = await handler.Handle(new CreateGroupCommand(
            Name: $"Drivable_{Guid.NewGuid():N}", Description: null,
            MemberIds: [], RoleIds: [role.Id],
            ExternallyDrivable: true), ct);

        Assert.False(result.IsError);
        // The handler returns the projected document — proves PrincipalProjectionBase
        // materializes the flag (the seam the original integration map omitted).
        Assert.True(result.Value.ExternallyDrivable);
    }

    [Fact]
    public async Task CreateGroup_Default_ExternallyDrivable_Is_False()
    {
        var ct = TestContext.Current.CancellationToken;

        using var scope = Factory.Services.CreateScope();
        var handler = NewCreateGroupHandler(scope.ServiceProvider);

        var result = await handler.Handle(new CreateGroupCommand(
            Name: $"Plain_{Guid.NewGuid():N}", Description: null,
            MemberIds: [], RoleIds: []), ct);

        Assert.False(result.IsError);
        Assert.False(result.Value.ExternallyDrivable);
    }

    [Fact]
    public async Task UpdateGroup_ExternallyDrivable_With_RealmAdminRole_Is_Rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var adminRole = await Factory.CreateTestRoleAsync(
            $"RealmAdmin_{Guid.NewGuid():N}", isRealmAdmin: true);
        var group = await Factory.CreateTestGroupAsync(
            name: $"Plain_{Guid.NewGuid():N}", memberIds: [], roleIds: []);

        using var scope = Factory.Services.CreateScope();
        var handler = NewUpdateGroupHandler(scope.ServiceProvider);

        var result = await handler.Handle(new UpdateGroupCommand(
            Id: group.Id, Name: group.Name, Description: null,
            MemberIds: [], RoleIds: [adminRole.Id],
            ExternallyDrivable: true), ct);

        Assert.True(result.IsError);
        Assert.Contains(result.Errors, e => e.Code == "Group.ExternallyDrivableRealmAdmin");
    }

    [Fact]
    public async Task LoginProvider_Federation_Flags_RoundTrip_Through_Events()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();

        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Events.StartStream<LoginProvider>(id, new LoginProviderAddedEvent(
            Id: id,
            Type: LoginProviderType.Oidc,
            Flavor: LoginProviderFlavor.GenericOidc,
            Slug: $"fed-{Guid.NewGuid():N}"[..20],
            DisplayName: $"Fed_{Guid.NewGuid():N}",
            Description: null,
            IsBuiltIn: false,
            Enabled: false,
            ClientId: "client",
            ClientSecretEncrypted: null,
            Scopes: ["openid"],
            UserUpdateScript: string.Empty,
            StoreRawClaims: false,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: false,
            AllowLinking: true,
            TrustForEmailLink: false,
            AllowedEmailDomains: null,
            IconName: null,
            ButtonColorHex: null,
            FlavorData: null,
            CreatedAt: DateTimeOffset.UtcNow,
            TrustForAuthorization: true,
            AuthoritativeForProfile: true));
        await session.SaveChangesAsync(ct);

        var created = await session.LoadAsync<LoginProvider>(id, ct);
        Assert.NotNull(created);
        Assert.True(created!.TrustForAuthorization);
        Assert.True(created.AuthoritativeForProfile);

        // Full-replace update flips one flag — the projection must apply it.
        session.Events.Append(id, new LoginProviderUpdatedEvent(
            Id: id,
            DisplayName: created.DisplayName,
            Description: null,
            ClientId: created.ClientId,
            Scopes: created.Scopes,
            UserUpdateScript: created.UserUpdateScript,
            StoreRawClaims: false,
            RawClaimsRetentionDays: null,
            AutoCreateUsers: false,
            AllowLinking: true,
            TrustForEmailLink: false,
            AllowedEmailDomains: null,
            IconName: null,
            ButtonColorHex: null,
            FlavorData: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            TrustForAuthorization: false,
            AuthoritativeForProfile: true));
        await session.SaveChangesAsync(ct);

        var updated = await session.LoadAsync<LoginProvider>(id, ct);
        Assert.NotNull(updated);
        Assert.False(updated!.TrustForAuthorization);
        Assert.True(updated.AuthoritativeForProfile);
    }

    private static CreateGroupHandler NewCreateGroupHandler(IServiceProvider sp) => new(
        sp.GetRequiredService<IDocumentSession>(),
        sp.GetRequiredService<IMembershipEvaluator>(),
        sp.GetRequiredService<IAutoMembershipRecalculator>());

    private static UpdateGroupHandler NewUpdateGroupHandler(IServiceProvider sp) => new(
        sp.GetRequiredService<IDocumentSession>(),
        sp.GetRequiredService<IMembershipEvaluator>(),
        sp.GetRequiredService<IPermissionService>(),
        sp.GetRequiredService<IAutoMembershipRecalculator>());
}
