using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Services;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using JasperFx;
using Testcontainers.PostgreSql;

namespace Cocoar.Auth.Tests.Infrastructure;

public class CocoarAuthWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("cocoar_auth_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new OptionalJsonConverterFactory(),
            new ShortGuidJsonConverter()
        },
        TypeInfoResolver = new OptionalAwareTypeInfoResolver()
    };

    public HttpClient CreateClientWithCookies()
    {
        var options = new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        };

        return CreateClient(options);
    }

    public void ClearCookies()
    {
        // Cookie clearing is handled via CleanDatabaseAsync
        // creating a fresh client with new options
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Remove existing Marten configuration and reconfigure with test container
            services.RemoveAll<IDocumentStore>();
            services.RemoveAll<IDocumentSession>();

            services.AddMarten(options =>
            {
                options.Connection(_postgresContainer.GetConnectionString());
                options.AutoCreateSchemaObjects = AutoCreate.All;

                options.Schema.For<ApplicationUser>()
                    .Identity(x => x.Id)
                    .Index(x => x.NormalizedUserName!, x => x.IsUnique = true)
                    .Index(x => x.NormalizedEmail!);

                options.Schema.For<ApplicationRole>()
                    .Identity(x => x.Id)
                    .Index(x => x.NormalizedName, x => x.IsUnique = true);
            })
            .UseLightweightSessions();
        });

        builder.UseEnvironment("Testing");
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
        await base.DisposeAsync();
    }

    public async Task<ApplicationUser> CreateTestUserAsync(
        string userName = "testuser",
        string password = "Test123!@#",
        string? email = null,
        bool isAdmin = false)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var user = new ApplicationUser(userName, email ?? $"{userName}@test.com");
        user.SetFirstName("Test");
        user.SetLastName("User");

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new Exception($"Failed to create test user: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }

        if (isAdmin)
        {
            var adminRole = await roleManager.FindByNameAsync("Admin");
            if (adminRole is null)
            {
                adminRole = new ApplicationRole("Admin", "Administrator role");
                await roleManager.CreateAsync(adminRole);
            }
            await userManager.AddToRoleAsync(user, "Admin");
        }

        return user;
    }

    public async Task<ApplicationRole> CreateTestRoleAsync(string name = "TestRole", string? description = null)
    {
        using var scope = Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var role = new ApplicationRole(name, description);
        var result = await roleManager.CreateAsync(role);

        if (!result.Succeeded)
        {
            throw new Exception($"Failed to create test role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        }

        return role;
    }

    public MockEmailSender GetMockEmailSender()
    {
        return Services.GetRequiredService<MockEmailSender>();
    }

    public async Task CleanDatabaseAsync()
    {
        ClearCookies();

        // Clear mock email sender
        var emailSender = GetMockEmailSender();
        emailSender.Clear();

        using var scope = Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.DeleteWhere<ApplicationUser>(u => true);
        session.DeleteWhere<ApplicationRole>(r => true);
        await session.SaveChangesAsync();
    }
}
