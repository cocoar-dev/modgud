using Cocoar.Auth.Api.Features.Admin;
using Cocoar.Auth.Domain.Realms;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Cocoar.Auth.Tests.Unit.Api.Features.Admin;

/// <summary>
/// Pinning tests for the small pure pieces of <see cref="RealmsEndpoints"/>:
/// the DTO mapping that gets sent to the admin UI, and the
/// <see cref="RequireCanManageTenantsFilter"/> that blocks realm-management
/// calls from non-management realms (security-relevant — without this, any
/// tenant could create / delete other tenants).
/// </summary>
public class RealmsEndpointsTests
{
    public class MapToDto
    {
        [Fact]
        public void Copies_all_fields_one_to_one()
        {
            var id = Guid.NewGuid();
            var created = DateTimeOffset.UtcNow;
            var realm = new Realm
            {
                Id = id,
                Slug = "acme",
                DisplayName = "Acme Inc",
                Description = "Tenant A",
                Domains = new[] { "acme.localhost", "auth.acme.com" },
                CanManageTenants = true,
                IsActive = true,
                CreatedAt = created,
            };

            var dto = RealmsEndpoints.MapToDto(realm);

            Assert.Equal(id, dto.Id);
            Assert.Equal("acme", dto.Slug);
            Assert.Equal("Acme Inc", dto.DisplayName);
            Assert.Equal("Tenant A", dto.Description);
            Assert.Equal(new[] { "acme.localhost", "auth.acme.com" }, dto.Domains);
            Assert.True(dto.CanManageTenants);
            Assert.True(dto.IsActive);
            Assert.Equal(created, dto.CreatedAt);
        }

        [Fact]
        public void NeedsSetup_is_always_false_in_current_etappe()
        {
            // Per-realm setup detection is intentionally deferred — the field is
            // wired through to the SPA so it stays stable, but always false today.
            // Pinning keeps the contract in lockstep with the comment in the source.
            var dto = RealmsEndpoints.MapToDto(new Realm { Id = Guid.NewGuid(), Slug = "x", DisplayName = "X" });
            Assert.False(dto.NeedsSetup);
        }
    }

    public class RequireCanManageTenantsFilterTests
    {
        private static EndpointFilterInvocationContext BuildContext(TenantInfo? tenantInfo)
        {
            var http = new DefaultHttpContext();
            if (tenantInfo is not null)
                http.Items[TenantConstants.HttpContextTenantInfoKey] = tenantInfo;
            return new DefaultEndpointFilterInvocationContext(http);
        }

        private static EndpointFilterDelegate NextReturning(object? value)
            => _ => ValueTask.FromResult(value);

        [Fact]
        public async Task Returns_404_when_no_tenant_info_resolved()
        {
            var filter = new RequireCanManageTenantsFilter();

            var result = await filter.InvokeAsync(BuildContext(tenantInfo: null), NextReturning("would-have-run"));

            Assert.IsType<NotFound>(result);
        }

        [Fact]
        public async Task Returns_404_when_tenant_cannot_manage_other_tenants()
        {
            // Realm management endpoints are mounted on every tenant (so the URL
            // is uniform) but only callable from realms with CanManageTenants — the
            // 404 (not 403) keeps the existence of the endpoint hidden from
            // unauthorized realms.
            var filter = new RequireCanManageTenantsFilter();
            var info = new TenantInfo("acme", CanManageTenants: false, IsActive: true);

            var result = await filter.InvokeAsync(BuildContext(info), NextReturning("would-have-run"));

            Assert.IsType<NotFound>(result);
        }

        [Fact]
        public async Task Calls_next_when_tenant_can_manage_other_tenants()
        {
            var filter = new RequireCanManageTenantsFilter();
            var info = new TenantInfo("system", CanManageTenants: true, IsActive: true);
            var sentinel = new object();

            var result = await filter.InvokeAsync(BuildContext(info), NextReturning(sentinel));

            Assert.Same(sentinel, result);
        }

        [Fact]
        public async Task Inactive_management_realm_is_still_allowed_through()
        {
            // The active-realm gate lives elsewhere (RealmMiddleware itself);
            // the filter cares only about CanManageTenants. Pinning so a future
            // change here is a deliberate decision, not a quiet drift.
            var filter = new RequireCanManageTenantsFilter();
            var info = new TenantInfo("system", CanManageTenants: true, IsActive: false);
            var sentinel = new object();

            var result = await filter.InvokeAsync(BuildContext(info), NextReturning(sentinel));

            Assert.Same(sentinel, result);
        }
    }
}
