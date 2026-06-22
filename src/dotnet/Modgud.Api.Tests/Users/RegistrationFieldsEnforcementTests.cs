using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Helper;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.DTOs.User;
using Modgud.Domain.Common;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// Configurable required-identity-field policy enforced on the admin create/edit
/// paths (the realm-level <c>RegistrationFields</c> section). The default (all
/// Optional) is the zero-behaviour baseline covered by <see cref="UserCrudTests"/>;
/// these pin the Required / Off behaviours.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class RegistrationFieldsEnforcementTests : IntegrationTestBase
{
    public RegistrationFieldsEnforcementTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Create_Rejects_Missing_Required_Firstname()
    {
        await SetPolicyAsync(firstname: "Required");

        var resp = await Client.PostAsJsonAsync("/api/user",
            new UserCreateDto { Email = "needs-first@test.com", Lastname = "Doe" },
            JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Succeeds_When_Required_Firstname_Present()
    {
        await SetPolicyAsync(firstname: "Required");

        var resp = await Client.PostAsJsonAsync("/api/user",
            new UserCreateDto { Email = "has-first@test.com", Firstname = "Ada", Lastname = "Doe" },
            JsonOptions, TestContext.Current.CancellationToken);

        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Create_Rejects_Blank_Username_When_Required()
    {
        await SetPolicyAsync(username: "Required");

        // No username supplied → under the Required policy this is rejected rather
        // than defaulted to the email.
        var resp = await Client.PostAsJsonAsync("/api/user",
            new UserCreateDto { Email = "needs-uname@test.com" },
            JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Forces_Username_To_Email_When_Off()
    {
        await SetPolicyAsync(username: "Off");

        var resp = await Client.PostAsJsonAsync("/api/user",
            new UserCreateDto { Email = "off-uname@test.com", UserName = "ignored-custom" },
            JsonOptions, TestContext.Current.CancellationToken);

        resp.EnsureSuccessStatusCode();
        var dto = await resp.ReadSuccessJsonAsync<UserDto>(JsonOptions);
        // Username=Off → the supplied username is ignored; the email is the username.
        Assert.Equal("off-uname@test.com", dto.UserName);
    }

    [Fact]
    public async Task Update_Rejects_Clearing_A_Required_Name()
    {
        await SetPolicyAsync(lastname: "Required");
        var user = await Factory.CreateTestUserAsync("John", "Doe", "JD");
        var userId = new ShortGuid(user.Id).ToString();

        var resp = await Client.PutAsJsonAsync($"/api/user/{userId}",
            new UserUpdateDto { Lastname = new Optional<string>("") },
            JsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Sets the full policy each time so a prior test's partial patch can't leak in.
    private async Task SetPolicyAsync(string? username = null, string? firstname = null, string? lastname = null)
    {
        var patch = new UpdateRealmSettingsDto
        {
            RegistrationFields = new UpdateRegistrationFieldsSettingsDto
            {
                Username = username ?? "Optional",
                Firstname = firstname ?? "Optional",
                Lastname = lastname ?? "Optional",
            },
        };
        var resp = await Client.PatchAsJsonAsync(
            "/api/admin/realm-settings", patch, JsonOptions, TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
    }
}
