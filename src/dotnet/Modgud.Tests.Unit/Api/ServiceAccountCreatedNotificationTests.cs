using System.Reactive.Linq;
using System.Text.Json;
using BuildingBlocks.EventDispatcher;
using Modgud.Api.Cluster;
using Modgud.Api.Features.ServiceAccounts;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Domain.ValueObjects;

namespace Modgud.Tests.Unit.Api;

/// <summary>
/// B7-SC01: the creator's one-time credential response must not become a read-stream
/// or cluster-relay payload. No host, database, SignalR transport or network is used.
/// </summary>
public class ServiceAccountCreatedNotificationTests
{
    private const string Secret = "one-time-test-secret-not-for-streams";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Creator_receives_the_initial_credential_without_mutating_the_event_dto(bool isActive)
    {
        var created = CreateResult(isActive);

        var response = ServiceAccountsEndpoints.CompleteCreateWithInitialCredential(
            created, new DataEventDispatcher(), "acme");

        Assert.NotSame(created.CreatedServiceAccount, response);
        Assert.Equal(created.CreatedServiceAccount!.Id, response.Id);
        Assert.Equal(created.CreatedServiceAccount.AccountName, response.AccountName);
        Assert.Equal(created.CreatedServiceAccount.Purpose, response.Purpose);
        Assert.Equal(isActive, response.IsActive);
        Assert.Equal(EntityStatus.Pending, response.Status);
        Assert.Null(created.CreatedServiceAccount.InitialCredential);
        Assert.NotNull(response.InitialCredential);
        Assert.Same(created.Client, response.InitialCredential.Credential);
        Assert.Equal(Secret, response.InitialCredential.ClientSecret);
        Assert.Contains(Secret, JsonSerializer.Serialize(response));
    }

    [Fact]
    public void Local_notifications_keep_both_subjects_tenant_and_public_account_fields_without_the_secret()
    {
        var dispatcher = new DataEventDispatcher();
        var received = new List<DataEvent>();
        var immediateJson = new List<string>();
        using var subscription = dispatcher.Notifications.Subscribe(ev =>
        {
            received.Add(ev);
            immediateJson.Add(JsonSerializer.Serialize(ev.Payload));
        });
        var created = CreateResult(isActive: false);

        var response = ServiceAccountsEndpoints.CompleteCreateWithInitialCredential(created, dispatcher, "acme");

        Assert.Collection(received,
            ev =>
            {
                Assert.Equal("OAuthClient", ev.Subject);
                Assert.Same(created.Client, Assert.Single(ev.Payload));
            },
            ev =>
            {
                Assert.Equal("ServiceAccount", ev.Subject);
                var account = Assert.IsType<ServiceAccountDto>(Assert.Single(ev.Payload));
                Assert.NotSame(created.CreatedServiceAccount, account);
                Assert.NotSame(response, account);
                Assert.Equal(response.Id, account.Id);
                Assert.Equal(response.AccountName, account.AccountName);
                Assert.Equal(response.Purpose, account.Purpose);
                Assert.Equal(response.IsActive, account.IsActive);
                Assert.Equal(response.Status, account.Status);
                Assert.Null(account.InitialCredential);
            });
        Assert.All(received, ev =>
        {
            Assert.Equal(DataEventAction.Created, ev.Action);
            Assert.Equal("acme", ev.Tenant);
        });
        Assert.All(immediateJson, json => Assert.DoesNotContain(Secret, json));
        // Check again after the response was enriched: dispatching first and then
        // adding the secret to the same DTO would still leak through retained references.
        Assert.All(received, ev => Assert.DoesNotContain(Secret, JsonSerializer.Serialize(ev.Payload)));
    }

    [Fact]
    public void Delayed_relay_encoding_and_round_trip_do_not_pick_up_response_credentials()
    {
        var relay = new DeferredRelay();
        var created = CreateResult(isActive: true);
        var response = ServiceAccountsEndpoints.CompleteCreateWithInitialCredential(
            created, new DataEventDispatcher(relay), "acme");

        Assert.NotNull(response.InitialCredential);
        Assert.Equal(Secret, response.InitialCredential.ClientSecret);
        Assert.Equal(2, relay.Events.Count);
        foreach (var ev in relay.Events)
        {
            var wire = JsonSerializer.Serialize(DataEventEnvelope.Encode(ev, "node-a"), DataEventEnvelope.WireJson);
            Assert.DoesNotContain(Secret, wire);
            Assert.DoesNotContain("\"ClientSecret\"", wire);
            var envelope = JsonSerializer.Deserialize<DataEventEnvelope>(wire, DataEventEnvelope.WireJson)!;
            var decoded = DataEventEnvelope.Decode(envelope, "node-b");
            Assert.NotNull(decoded);
            Assert.Equal("acme", decoded.Tenant);
            Assert.Equal(ev.Subject, decoded.Subject);
            Assert.Equal(DataEventAction.Created, decoded.Action);
            if (decoded.Subject == "ServiceAccount")
            {
                var account = Assert.IsType<ServiceAccountDto>(Assert.Single(decoded.Payload));
                Assert.Equal(response.Id, account.Id);
                Assert.Null(account.InitialCredential);
            }
        }
    }

    [Fact]
    public void An_enriched_service_result_is_not_forwarded_or_mutated()
    {
        var created = CreateResult(isActive: true);
        created.CreatedServiceAccount!.InitialCredential = new ServiceAccountCredentialIssuedDto
        {
            Credential = created.Client,
            ClientSecret = "another-response-only-secret",
        };
        var relay = new DeferredRelay();

        var response = ServiceAccountsEndpoints.CompleteCreateWithInitialCredential(
            created, new DataEventDispatcher(relay), "acme");

        Assert.Equal(Secret, response.InitialCredential!.ClientSecret);
        Assert.Equal("another-response-only-secret", created.CreatedServiceAccount.InitialCredential.ClientSecret);
        Assert.All(relay.Events, ev =>
        {
            var wire = JsonSerializer.Serialize(DataEventEnvelope.Encode(ev, "node-a"), DataEventEnvelope.WireJson);
            Assert.DoesNotContain(Secret, wire);
            Assert.DoesNotContain("another-response-only-secret", wire);
            Assert.DoesNotContain("\"ClientSecret\"", wire);
        });
    }

    private static OAuthClientCreatedDto CreateResult(bool isActive) => new()
    {
        Client = new OAuthClientDto
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = "test-service.credential",
            ClientType = "confidential",
            ConsentType = "implicit",
            RedirectUris = [],
            PostLogoutRedirectUris = [],
            Permissions = [],
        },
        ClientSecret = Secret,
        CreatedServiceAccount = new ServiceAccountDto
        {
            Id = Guid.NewGuid().ToString(),
            AccountName = "test-service",
            Purpose = "Notification regression",
            IsActive = isActive,
            Status = EntityStatus.Pending,
        },
    };

    private sealed class DeferredRelay : IDataEventRelay
    {
        public List<DataEvent> Events { get; } = [];

        public ValueTask PublishAsync(DataEvent ev, CancellationToken cancellationToken = default)
        {
            // Retain the actual payload references, as an asynchronous serializer can.
            Events.Add(ev);
            return ValueTask.CompletedTask;
        }
    }
}
