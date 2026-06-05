using System.Text.Json;
using Cocoar.Configuration.Testing;

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
}
