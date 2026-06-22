using System.Net;
using System.Net.Http.Json;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.RealmSettings;

namespace Modgud.Api.Tests.Users;

/// <summary>
/// Per-realm registration-field requirement policy on <c>RealmSettings</c>
/// (configurable required identity fields). Verifies the new
/// <c>RegistrationFields</c> sub-section reads its lenient defaults (all
/// Optional — today's behaviour), persists a valid partial patch field-by-field,
/// and rejects an unknown requirement value.
/// </summary>
public class RegistrationFieldsSettingsTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Endpoint = "/api/admin/realm-settings";

    [Fact]
    public async Task Defaults_are_all_optional_when_never_configured()
    {
        var dto = await Client.GetFromJsonAsync<RealmSettingsDto>(
            Endpoint, JsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(dto);
        Assert.Equal("Optional", dto!.RegistrationFields.Username);
        Assert.Equal("Optional", dto.RegistrationFields.Firstname);
        Assert.Equal("Optional", dto.RegistrationFields.Lastname);
    }

    [Fact]
    public async Task Valid_partial_patch_persists_field_by_field()
    {
        var patch = new UpdateRealmSettingsDto
        {
            // Only touch two fields; Lastname must keep its default.
            RegistrationFields = new UpdateRegistrationFieldsSettingsDto
            {
                Username = "Off",
                Firstname = "Required",
            },
        };

        var resp = await Client.PatchAsJsonAsync(
            Endpoint, patch, JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await Client.GetFromJsonAsync<RealmSettingsDto>(
            Endpoint, JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal("Off", dto!.RegistrationFields.Username);
        Assert.Equal("Required", dto.RegistrationFields.Firstname);
        Assert.Equal("Optional", dto.RegistrationFields.Lastname); // untouched → default
    }

    [Fact]
    public async Task Patch_rejected_for_unknown_requirement_value()
    {
        var patch = new UpdateRealmSettingsDto
        {
            RegistrationFields = new UpdateRegistrationFieldsSettingsDto { Firstname = "Mandatory" },
        };

        var resp = await Client.PatchAsJsonAsync(
            Endpoint, patch, JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
