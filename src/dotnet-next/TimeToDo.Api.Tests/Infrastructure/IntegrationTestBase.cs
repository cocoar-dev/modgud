using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Configuration.Testing;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Api.Tests.Infrastructure;

/// <summary>
/// Base class for integration tests that provides common setup and utilities.
/// Uses the shared factory from SharedPostgresFixture (host created once).
/// Resets all Marten data between tests via ResetMartenDataAsync().
/// Authenticates via Cookie Auth (POST /api/account/login) — same as production.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public abstract class IntegrationTestBase : IAsyncLifetime, IDisposable
{
    private readonly SharedPostgresFixture _fixture;

    private const string DefaultPassword = "TestPass1234";

    protected TimeTodoWebApplicationFactory Factory = null!;
    protected HttpClient Client = null!;
    protected JsonSerializerOptions JsonOptions = null!;

    // Default test user that will be created for authentication
    protected UserView? DefaultUser;

    protected IntegrationTestBase(SharedPostgresFixture fixture)
    {
        _fixture = fixture;

        // Apply test configuration in constructor - this runs in the test's async context
        CocoarTestConfiguration.Apply(fixture.TestContext);
    }

    public async ValueTask InitializeAsync()
    {
        // Use the shared factory from the fixture (host created once, not per test)
        Factory = _fixture.Factory;
        JsonOptions = Factory.JsonOptions;

        // Initialize the host by creating a throwaway client (required before ResetMartenDataAsync)
        Factory.CreateClient().Dispose();

        // Reset all Marten data between tests
        await Factory.ResetMartenDataAsync();

        // Create a default test user with Identity + password + admin permission
        // Admin by default so existing tests keep working — security tests create their own non-admin users
        DefaultUser = await Factory.CreateTestUserWithIdentityAsync(
            firstname: "Test",
            lastname: "User",
            acronym: "TU",
            email: "test@test.com",
            password: DefaultPassword,
            permissions: ["app:admin"]);

        // Create authenticated client via cookie auth (same as production)
        Client = await CreateAuthenticatedClientAsync("tu", DefaultPassword);
    }

    /// <summary>
    /// Creates an HttpClient authenticated via Cookie Auth (POST /api/account/login).
    /// The client stores the session cookie and sends it with all subsequent requests.
    /// </summary>
    protected async Task<HttpClient> CreateAuthenticatedClientAsync(string userName, string password)
    {
        var cookieHandler = new CookieContainerHandler();
        var client = Factory.CreateDefaultClient(cookieHandler);

        var loginResponse = await client.PostAsJsonAsync("/api/account/login",
            new { UserName = userName, Password = password }, TestContext.Current.CancellationToken);

        if (!loginResponse.IsSuccessStatusCode)
        {
            var body = await loginResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Test login failed for '{userName}': {loginResponse.StatusCode} — {body}");
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        // Don't dispose Factory — it's shared via SharedPostgresFixture
    }

    public void Dispose()
    {
        // Clear test configuration when test class is disposed
        CocoarTestConfiguration.Clear();
    }
}

/// <summary>
/// DelegatingHandler that stores and forwards cookies — required for cookie auth in tests.
/// WebApplicationFactory.CreateDefaultClient() requires DelegatingHandler, not HttpClientHandler.
/// </summary>
internal class CookieContainerHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Add stored cookies to outgoing request
        var cookieHeader = _cookies.GetCookieHeader(request.RequestUri!);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.Add("Cookie", cookieHeader);

        var response = await base.SendAsync(request, cancellationToken);

        // Store cookies from response
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var cookie in setCookies)
                _cookies.SetCookies(request.RequestUri!, cookie);
        }

        return response;
    }
}
