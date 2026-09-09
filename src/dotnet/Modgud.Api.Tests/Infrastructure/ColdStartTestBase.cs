using System.Text.Json;
using Cocoar.Configuration.Testing;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Application.DTOs.Realms;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Base for cold-start tests. Unlike <see cref="IntegrationTestBase"/> it does
/// NOT create a default admin, log in, or reset Marten data — the host boots once
/// against a blank DB and tests observe the genuine cold state. Mirrors the
/// existing pattern of re-applying the fixture's config context in the ctor
/// (the host builds lazily in the test's async context, not the fixture's).
/// </summary>
[Collection(ColdStartCollection.Name)]
public abstract class ColdStartTestBase : IDisposable
{
    protected readonly ColdStartFixture Fixture;

    protected ColdStartWebApplicationFactory Factory => Fixture.Factory;
    protected JsonSerializerOptions JsonOptions => Fixture.Factory.JsonOptions;

    protected ColdStartTestBase(ColdStartFixture fixture)
    {
        Fixture = fixture;

        // Bridge the AsyncLocal gap between fixture setup and this test's context.
        CocoarTestConfiguration.Apply(fixture.TestContext);

        // Build the shared cold-boot host (idempotent) so Factory.Services is
        // usable and the cold-boot bootstrap has run.
        Fixture.Factory.CreateClient().Dispose();
    }

    public void Dispose() => CocoarTestConfiguration.Clear();

    /// <summary>
    /// Provisions a realm from scratch: create the shell, then fill it from the manifest.
    /// A manifest carries no realm identity, so these are two operations — this helper is
    /// the in-process twin of what a caller does over HTTP (<c>POST /api/admin/realms</c>
    /// then <c>POST /api/admin/realms/{slug}/apply</c>) and of
    /// <c>Modgud.Provisioning.TestKit</c>'s <c>ImportRealmAsync</c>.
    /// </summary>
    /// <remarks>
    /// Unlike the TestKit it does NOT tear the realm down when the manifest step fails —
    /// tests that assert on a failed apply want to inspect the realm it left behind.
    /// </remarks>
    protected static async Task<ErrorOr<RealmImportResult>> ProvisionRealmAsync(
        ColdStartWebApplicationFactory factory, CreateRealmDto realm, RealmManifest manifest,
        CancellationToken ct)
    {
        var created = await factory.Services
            .GetRequiredService<IRealmProvisioningService>().CreateRealmAsync(realm, ct);
        if (created.IsError) return created.Errors;

        return await factory.Services
            .GetRequiredService<RealmManifestApplier>().UpdateRealmAsync(realm.Slug, manifest, ct: ct);
    }

    /// <summary>The boilerplate realm shell most provisioning tests want: one routing
    /// domain and an initial admin derived from the slug.</summary>
    protected static CreateRealmDto Shell(string slug, string? displayName = null) => new()
    {
        Slug = slug,
        DisplayName = displayName ?? slug,
        Domains = [$"{slug}.localhost"],
        InitialAdmin = new InitialAdminDto { UserName = "boot", Email = $"boot@{slug}.test" },
    };
}
