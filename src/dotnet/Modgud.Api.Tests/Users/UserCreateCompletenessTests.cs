using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.User;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Tests.Users;

public class UserCreateCompletenessTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_commits_profile_membership_and_security_policy_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = await Factory.CreateTestGroupAsync($"Complete_{Guid.NewGuid():N}", []);
        var groupId = new ShortGuid(group.Id).ToString();

        var response = await Client.PostAsJsonAsync("/api/user", new
        {
            Firstname = "Ada",
            Lastname = "Complete",
            Acronym = "AC",
            Email = $"ada-{Guid.NewGuid():N}@test.com",
            UserName = $"ada-{Guid.NewGuid():N}",
            Password = "TestPass1234",
            EmailConfirmed = true,
            IsActive = true,
            GroupIds = new[] { groupId },
            GracePeriodDaysOverride = 30,
            TwoFactorExempt = true,
        }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct);
        Assert.NotNull(created);
        Assert.True(ShortGuid.TryParse(created.Id, out Guid userId));

        await Factory.WaitForProjectionsAsync();

        using var scope = Factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var storedGroup = await query.LoadAsync<Group>(group.Id, ct);
        var security = await query.LoadAsync<UserSecurityData>(userId, ct);

        Assert.Contains(userId, storedGroup!.MemberIds);
        Assert.Equal(30, security!.GracePeriodDaysOverride);
        Assert.True(security.TwoFactorExempt);
        Assert.False(string.IsNullOrWhiteSpace(security.PasswordHash));
    }

    [Fact]
    public async Task Create_with_invalid_group_writes_no_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"invalid-group-{Guid.NewGuid():N}@test.com";

        var response = await Client.PostAsJsonAsync("/api/user", new
        {
            Email = email,
            UserName = email,
            GroupIds = new[] { "not-a-group-id" },
        }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var person = await query.Query<Person>()
            .FirstOrDefaultAsync(p => p.NormalizedEmail == email.ToUpperInvariant(), ct);
        var applicationUser = await query.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == email.ToUpperInvariant(), ct);

        Assert.Null(person);
        Assert.Null(applicationUser);
    }
}
