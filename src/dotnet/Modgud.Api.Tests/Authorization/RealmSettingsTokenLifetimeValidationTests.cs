using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Authentication.RealmSettings;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// The per-realm JWT-access-token-flow sub-sections (DCR, CIMD, native grants)
/// all mint self-contained, individually-non-revocable JWT access tokens, so the
/// short access TTL is the only bound on a leaked token. <c>RealmSettingsService</c>
/// must reject degenerate / over-long lifetimes at write time rather than let an
/// admin configure an effectively-permanent token. These tests pin that shared
/// bounds validation for DCR and CIMD (the native-grant section is pinned in
/// <see cref="CocoarNativeGrantFlowTests"/>).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RealmSettingsTokenLifetimeValidationTests : IntegrationTestBase
{
    public RealmSettingsTokenLifetimeValidationTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Dcr_OutOfBandLifetimes_Rejected()
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();

        var tooLong = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Dcr = new UpdateDcrSettingsDto { Enabled = true, AccessTokenLifetimeMinutes = 525_600 },
        }, TestContext.Current.CancellationToken);
        Assert.True(tooLong.IsError);

        var zero = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Dcr = new UpdateDcrSettingsDto { Enabled = true, AccessTokenLifetimeMinutes = 0 },
        }, TestContext.Current.CancellationToken);
        Assert.True(zero.IsError);

        var badRefresh = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Dcr = new UpdateDcrSettingsDto { Enabled = true, RefreshTokenLifetimeDays = 9_999 },
        }, TestContext.Current.CancellationToken);
        Assert.True(badRefresh.IsError);

        // The sane defaults (Enabled only) must still patch cleanly.
        var ok = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Dcr = new UpdateDcrSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
        Assert.False(ok.IsError);
    }

    [Fact]
    public async Task Cimd_OutOfBandLifetimes_Rejected()
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();

        var tooLong = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Cimd = new UpdateCimdSettingsDto { Enabled = true, AccessTokenLifetimeMinutes = 525_600 },
        }, TestContext.Current.CancellationToken);
        Assert.True(tooLong.IsError);

        var zero = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Cimd = new UpdateCimdSettingsDto { Enabled = true, AccessTokenLifetimeMinutes = 0 },
        }, TestContext.Current.CancellationToken);
        Assert.True(zero.IsError);

        var badRefresh = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Cimd = new UpdateCimdSettingsDto { Enabled = true, RefreshTokenLifetimeDays = 9_999 },
        }, TestContext.Current.CancellationToken);
        Assert.True(badRefresh.IsError);

        // The sane defaults (Enabled only) must still patch cleanly.
        var ok = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Cimd = new UpdateCimdSettingsDto { Enabled = true },
        }, TestContext.Current.CancellationToken);
        Assert.False(ok.IsError);
    }

    [Fact]
    public async Task SecurityAuditRetention_OutsideRealmBounds_IsRejected()
    {
        using var scope = NewSystemTenantScope();
        var settings = scope.ServiceProvider.GetRequiredService<IRealmSettingsService>();
        var ct = TestContext.Current.CancellationToken;

        foreach (var invalidDays in new[] { 0, 366 })
        {
            var invalid = await settings.PatchAsync(new UpdateRealmSettingsDto
            {
                Audit = new UpdateAuditSettingsDto
                {
                    SecurityRetentionDays = invalidDays,
                },
            }, ct);
            Assert.True(invalid.IsError);
        }

        var valid = await settings.PatchAsync(new UpdateRealmSettingsDto
        {
            Audit = new UpdateAuditSettingsDto
            {
                SecurityRetentionDays = 30,
            },
        }, ct);

        Assert.False(valid.IsError);
        Assert.Equal(30, valid.Value.Audit.SecurityRetentionDays);
    }

    private IServiceScope NewSystemTenantScope()
    {
        var scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>()
            .HttpContext = new DefaultHttpContext { Items = { ["TenantId"] = "system" } };
        return scope;
    }
}
