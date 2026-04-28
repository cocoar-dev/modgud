using Microsoft.EntityFrameworkCore;

namespace TimeToDo.TestIdP;

/// <summary>
/// InMemory EF context purely so OpenIddict has a home for its application
/// (client) registrations. Rehydrated from the JSON config on every startup
/// via <c>SeedClientsHostedService</c> — the operator edits JSON, we adjust.
/// </summary>
public class TestIdpDbContext(DbContextOptions<TestIdpDbContext> options) : DbContext(options);
