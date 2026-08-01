using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cocoar.Configuration.Testing;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Modgud.Api.Tests.Infrastructure;

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
    private readonly IDisposable _tenantContext;

    private const string DefaultPassword = "TestPass1234";

    protected ModgudWebApplicationFactory Factory = null!;
    protected HttpClient Client = null!;
    protected JsonSerializerOptions JsonOptions = null!;

    // Default test user that will be created for authentication
    protected UserView? DefaultUser;

    protected IntegrationTestBase(SharedPostgresFixture fixture)
    {
        _fixture = fixture;

        // Apply test configuration in constructor - this runs in the test's async context
        CocoarTestConfiguration.Apply(fixture.TestContext);
        _tenantContext = TenantContext.Enter(TenantConstants.SystemTenantId);
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
            isRealmAdmin: true);

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

    /// <summary>
    /// Resolves <see cref="IMessageBus"/> from the given scope and pre-sets
    /// <c>TenantId</c> to the system tenant so the OutboxedSessionFactory can
    /// open a Marten session against MasterTableTenancy. HTTP-driven tests get
    /// the tenant set by <c>TenantContextMiddleware</c>; tests that resolve
    /// <c>IMessageBus</c> directly bypass the request pipeline and need this.
    /// </summary>
    protected static IMessageBus GetTenantedMessageBus(IServiceScope scope, string tenantId = "system")
    {
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        bus.TenantId = tenantId;
        return bus;
    }

    /// <summary>
    /// Opens a tenant-scoped Marten <see cref="IQuerySession"/>. The master
    /// <see cref="IDocumentStore"/> resolved from DI uses
    /// <c>MasterTableTenancy</c>, which has no <c>Default</c> session — calling
    /// <c>store.QuerySession()</c> throws "Default tenant does not supported".
    /// Use this helper from any test that needs to read tenant data outside
    /// the HTTP pipeline (the pipeline's <c>TenantContextMiddleware</c> sets
    /// the tenant from <c>HttpContext.Items["TenantId"]</c> for in-flight
    /// requests; this helper is the equivalent for arrange/assert blocks).
    /// </summary>
    protected IQuerySession GetTenantedSession(string tenantId = "system")
    {
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        return store.QuerySession(tenantId);
    }

    /// <summary>
    /// Same as <see cref="GetTenantedSession(string)"/> but returns a writeable
    /// <see cref="IDocumentSession"/>. Useful when an arrange block needs to
    /// seed test data outside the HTTP pipeline.
    /// </summary>
    protected IDocumentSession GetTenantedDocumentSession(string tenantId = "system")
    {
        var store = Factory.Services.GetRequiredService<IDocumentStore>();
        return store.LightweightSession(tenantId);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        // Don't dispose Factory — it's shared via SharedPostgresFixture
    }

    public void Dispose()
    {
        _tenantContext.Dispose();
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

    /// <summary>
    /// Pre-seed a cookie (e.g. a hand-forged auth cookie) so the very first
    /// request already carries it. Used by federated-login tests that build the
    /// ApplicationScheme cookie out-of-band instead of going through a real
    /// upstream IdP round-trip.
    /// </summary>
    public void Seed(Uri uri, string name, string value)
        => _cookies.Add(uri, new Cookie(name, value) { Path = "/" });

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
