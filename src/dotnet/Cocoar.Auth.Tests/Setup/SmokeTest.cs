using System.Net;
using System.Net.Http.Json;
using Cocoar.Auth.Tests.Infrastructure;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Cocoar.Auth.Tests;

/// <summary>
/// Minimal smoke test to verify the test infrastructure works.
/// This should be the FIRST test to pass before anything else.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SmokeTest : IAsyncLifetime
{
    private readonly SharedPostgresFixture _fixture;
    private CocoarAuthWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public SmokeTest(SharedPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _factory = new CocoarAuthWebApplicationFactory(_fixture);
        _client = _factory.CreateClientWithCookies();
        // Note: NOT cleaning database - this is just a smoke test
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Application_Starts_And_Responds()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Database_Connection_Works()
    {
        // Verify we can access services that depend on database
        using var scope = _factory.Services.CreateScope();
        var docSession = scope.ServiceProvider.GetService<Marten.IDocumentSession>();

        Assert.NotNull(docSession);

        // Try a simple query - should not throw
        var count = await docSession.Query<Cocoar.Auth.Domain.Entities.ApplicationUser>().CountAsync();
        Assert.True(count >= 0); // Just verifying query works
    }

    [Fact]
    public async Task Api_Endpoint_RespondsSuccessfully()
    {
        // Test an actual API endpoint - just verify the API layer is working
        var loginRequest = new { userName = "nonexistent", password = "wrong" };
        var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // We just want to prove the API is reachable and processes requests
        // The actual status code depends on implementation details
        Assert.True(
            response.StatusCode != HttpStatusCode.InternalServerError,
            $"API returned 500: {await response.Content.ReadAsStringAsync()}");
    }
}
