using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Positions;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.Services;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Management;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Point-4 consumer contract: a trusted Service Account provisions a terminal,
/// its managed OAuth client and optionally its Position in one request. The
/// caller-selected client id is the retry key and the Service Account remains
/// the recorded actor.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class TerminalProvisioningManagementTests : IntegrationTestBase
{
    private const string StaffingGrant = "urn:cocoar:params:oauth:grant-type:staffing";
    private const string RpId = "terminal-consumer.localhost";

    public TerminalProvisioningManagementTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Service_account_can_provision_an_existing_position_in_one_call()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var position = await CreateEnabledPositionAsync($"provision-existing-{Guid.NewGuid():N}");
        var caller = await CreateManagementCallerAsync(
            ("position", "write"), ("oauth-client", "write"));
        var clientId = $"consumer-terminal-{Guid.NewGuid():N}";
        var request = ExistingPositionRequest(clientId, position.Id);

        using var response = await SendProvisioningAsync(caller.Token, request);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = JsonSerializer.Deserialize<OAuthClientCreatedDto>(body, JsonOptions)!;
        Assert.Equal(clientId, created.Client.ClientId);
        Assert.False(created.WasAlreadyProvisioned);
        Assert.Null(created.CreatedPosition);
        Assert.True(ShortGuid.TryParse(created.CreatedTerminalId!, out Guid terminalId));

        await using var query = GetTenantedSession();
        var terminal = await query.LoadAsync<TerminalEnrollment>(terminalId, ct);
        Assert.NotNull(terminal);
        Assert.Equal(caller.PrincipalId, terminal.CreatedByUserId);
        Assert.Equal(new ShortGuid(position.Id).Guid, terminal.PositionPrincipalId);
        Assert.Equal(clientId, terminal.ClientId);

        var client = await query.Query<OAuthApplicationState>()
            .SingleAsync(candidate => candidate.ClientId == clientId, ct);
        Assert.Equal(terminalId, client.ManagedTerminalEnrollmentId);
        Assert.True(client.Properties.ContainsKey(
            OAuthApplicationPropertyKeys.TerminalProvisioningFingerprint));
    }

    [Fact]
    public async Task Service_account_can_create_position_terminal_and_client_atomically()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var caller = await CreateManagementCallerAsync(
            ("position", "write"), ("oauth-client", "write"));
        var clientId = $"consumer-inline-{Guid.NewGuid():N}";
        var accountName = $"inline-position-{Guid.NewGuid():N}";
        var request = new CreateOAuthClientDto
        {
            ClientId = clientId,
            ClientType = OAuthClientTypes.Public,
            DisplayName = "Consumer inline terminal",
            AllowedGrantTypes = [StaffingGrant],
            NewPosition = new PositionCreateDto
            {
                AccountName = accountName,
                Purpose = "Provisioned by a consumer",
                TerminalPolicy = new PositionTerminalPolicyUpdateDto { Enabled = true },
            },
            TerminalDisplayName = "Reception terminal",
            TerminalLocation = "Reception",
            TerminalBinding = DeviceBindingIds.Dpop,
            WebAuthnRpId = RpId,
        };

        using var response = await SendProvisioningAsync(caller.Token, request);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = JsonSerializer.Deserialize<OAuthClientCreatedDto>(body, JsonOptions)!;
        Assert.Equal(accountName, created.CreatedPosition!.AccountName);
        Assert.True(ShortGuid.TryParse(created.CreatedTerminalId!, out Guid terminalId));

        await using var query = GetTenantedSession();
        var terminal = await query.LoadAsync<TerminalEnrollment>(terminalId, ct);
        Assert.NotNull(terminal);
        var position = await query.LoadAsync<Modgud.Authorization.Principals.PositionPrincipal>(
            terminal.PositionPrincipalId, ct);
        Assert.Equal(accountName, position!.AccountName);
        Assert.Equal(caller.PrincipalId, terminal.CreatedByUserId);
    }

    [Fact]
    public async Task Identical_client_secret_retry_returns_the_same_terminal_but_never_the_secret_again()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var position = await CreateEnabledPositionAsync(
            $"provision-retry-{Guid.NewGuid():N}", DeviceBindingIds.ClientSecret);
        var caller = await CreateManagementCallerAsync(
            ("position", "write"), ("oauth-client", "write"));
        var clientId = $"consumer-retry-{Guid.NewGuid():N}";
        var request = ExistingPositionRequest(
            clientId, position.Id, DeviceBindingIds.ClientSecret);

        using var firstResponse = await SendProvisioningAsync(caller.Token, request);
        var firstBody = await firstResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = JsonSerializer.Deserialize<OAuthClientCreatedDto>(firstBody, JsonOptions)!;
        Assert.False(string.IsNullOrEmpty(first.ClientSecret));

        using var retryResponse = await SendProvisioningAsync(caller.Token, request);
        var retryBody = await retryResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var retry = JsonSerializer.Deserialize<OAuthClientCreatedDto>(retryBody, JsonOptions)!;
        Assert.True(retry.WasAlreadyProvisioned);
        Assert.Equal(first.CreatedTerminalId, retry.CreatedTerminalId);
        Assert.Null(retry.ClientSecret);

        await using var query = GetTenantedSession();
        var matching = await query.Query<OAuthApplicationState>()
            .Where(candidate => candidate.ClientId == clientId && !candidate.IsDeleted)
            .ToListAsync(ct);
        Assert.Single(matching);
    }

    [Fact]
    public async Task Same_client_id_with_different_terminal_intent_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var position = await CreateEnabledPositionAsync($"provision-conflict-{Guid.NewGuid():N}");
        var caller = await CreateManagementCallerAsync(
            ("position", "write"), ("oauth-client", "write"));
        var clientId = $"consumer-conflict-{Guid.NewGuid():N}";
        var request = ExistingPositionRequest(clientId, position.Id);

        using var first = await SendProvisioningAsync(caller.Token, request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var changed = request with { TerminalDisplayName = "A different physical terminal" };
        using var second = await SendProvisioningAsync(caller.Token, changed);
        var body = await second.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already exists", body);
    }

    [Fact]
    public async Task Terminal_provisioning_requires_both_management_permissions()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var position = await CreateEnabledPositionAsync($"provision-permission-{Guid.NewGuid():N}");
        var request = ExistingPositionRequest(
            $"consumer-permission-{Guid.NewGuid():N}", position.Id);

        var onlyClientWrite = await CreateManagementCallerAsync(("oauth-client", "write"));
        using var missingPosition = await SendProvisioningAsync(onlyClientWrite.Token, request);
        var missingPositionBody = await missingPosition.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, missingPosition.StatusCode);
        Assert.Contains("position:write", missingPositionBody);
        Assert.Contains("Management.PermissionDenied", missingPositionBody);

        var onlyPositionWrite = await CreateManagementCallerAsync(("position", "write"));
        using var missingClient = await SendProvisioningAsync(onlyPositionWrite.Token, request);
        var missingClientBody = await missingClient.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, missingClient.StatusCode);
        Assert.Contains("oauth-client:write", missingClientBody);
        Assert.Contains("Management.PermissionDenied", missingClientBody);
    }

    [Fact]
    public async Task OAuth_client_write_cannot_mint_a_credential_for_another_service_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = await CreateManagementCallerAsync(("oauth-client", "write"));
        using var targetResponse = await Client.PostAsJsonAsync(
            "/api/service-account",
            new { AccountName = $"credential-target-{Guid.NewGuid():N}" },
            JsonOptions,
            ct);
        var targetBody = await targetResponse.Content.ReadAsStringAsync(ct);
        Assert.True(targetResponse.IsSuccessStatusCode, targetBody);
        var target = JsonSerializer.Deserialize<ServiceAccountDto>(targetBody, JsonOptions)!;

        var request = new CreateOAuthClientDto
        {
            ClientId = $"foreign-sa-credential-{Guid.NewGuid():N}",
            ClientType = OAuthClientTypes.Confidential,
            DisplayName = "Must not be created",
            AllowedGrantTypes = ["client_credentials"],
            LinkedServiceAccountId = target.Id,
        };

        using var response = await SendProvisioningAsync(caller.Token, request);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("service-account:write", body);
        Assert.Contains("Management.PermissionDenied", body);
    }

    [Fact]
    public async Task Terminal_provisioning_requires_a_consumer_selected_client_id()
    {
        var ct = TestContext.Current.CancellationToken;
        SetFeatureFlag(true);
        var position = await CreateEnabledPositionAsync($"provision-client-id-{Guid.NewGuid():N}");
        var caller = await CreateManagementCallerAsync(
            ("position", "write"), ("oauth-client", "write"));
        var request = ExistingPositionRequest("", position.Id);

        using var response = await SendProvisioningAsync(caller.Token, request);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("caller-selected client id", body);
    }

    [Fact]
    public async Task Terminal_provisioning_stays_dark_while_the_feature_flag_is_off()
    {
        SetFeatureFlag(true);
        var position = await CreateEnabledPositionAsync($"provision-dark-{Guid.NewGuid():N}");
        var caller = await CreateManagementCallerAsync(
            ("position", "write"), ("oauth-client", "write"));
        SetFeatureFlag(false);
        try
        {
            using var response = await SendProvisioningAsync(
                caller.Token,
                ExistingPositionRequest($"consumer-dark-{Guid.NewGuid():N}", position.Id));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            SetFeatureFlag(true);
        }
    }

    [Fact]
    public async Task Provisioned_short_terminal_id_is_accepted_by_terminal_routes()
    {
        var ct = TestContext.Current.CancellationToken;
        var shortId = new ShortGuid(Guid.NewGuid()).ToString();
        using var response = await Client.PostAsync(
            $"/connect/staffing/{shortId}/lock", content: null, ct);

        // No token was supplied. Unauthorized proves the ShortGuid route was
        // matched; the old Guid-only constraint returned 404 before auth ran.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private void SetFeatureFlag(bool enabled) =>
        Factory.Services.GetRequiredService<AppSettings>().Features.PositionTerminals = enabled;

    private async Task<PositionPrincipalDto> CreateEnabledPositionAsync(
        string accountName,
        string binding = DeviceBindingIds.Dpop)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await Client.PostAsJsonAsync("/api/position", new
        {
            AccountName = accountName,
            TerminalPolicy = new
            {
                Enabled = true,
                AllowedDeviceBindings = new[] { binding },
            },
        }, JsonOptions, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode,
            $"position arrange failed ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<PositionPrincipalDto>(body, JsonOptions)!;
    }

    private async Task<ManagementCaller> CreateManagementCallerAsync(
        params (string Resource, string Action)[] permissions)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        using var createResponse = await Client.PostAsJsonAsync(
            "/api/service-account",
            new { AccountName = $"terminal-provisioner-{suffix}" },
            JsonOptions,
            ct);
        var createBody = await createResponse.Content.ReadAsStringAsync(ct);
        Assert.True(createResponse.IsSuccessStatusCode,
            $"service-account arrange failed ({(int)createResponse.StatusCode}): {createBody}");
        var serviceAccount = JsonSerializer.Deserialize<ServiceAccountDto>(createBody, JsonOptions)!;
        Assert.True(ShortGuid.TryParse(serviceAccount.Id, out Guid principalId));

        if (permissions.Length > 0)
        {
            var role = await Factory.CreateTestRoleAsync(
                $"TerminalProvisioner_{suffix}", permissions);
            await Factory.CreateTestGroupAsync(
                $"TerminalProvisioners_{suffix}", [principalId], [role.Id]);
        }

        var managementClientId = $"terminal-provisioner-client-{suffix}";
        using (var scope = Factory.Services.CreateScope())
        {
            var oauth = scope.ServiceProvider.GetRequiredService<OAuthAdminService>();
            var result = await oauth.CreateClientAsync(new CreateOAuthClientDto
            {
                ClientId = managementClientId,
                ClientSecret = $"{managementClientId}-secret",
                ClientType = OAuthClientTypes.Confidential,
                ConsentType = OAuthConsentTypes.Implicit,
                DisplayName = managementClientId,
                Scopes = [ModgudManagementApi.Scope],
                AllowedGrantTypes = ["client_credentials"],
                RequireConsent = false,
                AccessTokenType = AccessTokenType.Reference,
                LinkedServiceAccountId = serviceAccount.Id,
            }, ct);
            if (result.IsError)
                throw new InvalidOperationException(string.Join(", ",
                    result.Errors.Select(error => error.Description)));
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", managementClientId),
            new("client_secret", $"{managementClientId}-secret"),
            new("scope", ModgudManagementApi.Scope),
            new("resource", ModgudManagementApi.Audience),
        };
        using var tokenClient = Factory.CreateClient();
        using var tokenResponse = await tokenClient.PostAsync(
            "/connect/token", new FormUrlEncodedContent(form), ct);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);
        Assert.True(tokenResponse.IsSuccessStatusCode,
            $"client_credentials failed ({(int)tokenResponse.StatusCode}): {tokenBody}");
        using var tokenDocument = JsonDocument.Parse(tokenBody);
        return new ManagementCaller(
            principalId,
            tokenDocument.RootElement.GetProperty("access_token").GetString()!);
    }

    private static CreateOAuthClientDto ExistingPositionRequest(
        string clientId,
        string positionId,
        string binding = DeviceBindingIds.Dpop) => new()
    {
        ClientId = clientId,
        ClientType = binding == DeviceBindingIds.ClientSecret
            ? OAuthClientTypes.Confidential
            : OAuthClientTypes.Public,
        DisplayName = "Consumer terminal client",
        AllowedGrantTypes = [StaffingGrant],
        LinkedPositionPrincipalId = positionId,
        TerminalDisplayName = "Gate terminal left",
        TerminalLocation = "Gate 3",
        TerminalBinding = binding,
        WebAuthnRpId = RpId,
    };

    private async Task<HttpResponseMessage> SendProvisioningAsync(
        string token,
        CreateOAuthClientDto body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/oauth/clients")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var client = Factory.CreateClient();
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed record ManagementCaller(Guid PrincipalId, string Token);
}
