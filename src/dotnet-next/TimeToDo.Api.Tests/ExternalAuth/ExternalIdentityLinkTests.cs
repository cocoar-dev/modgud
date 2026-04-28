using System.Text.Json;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using TimeToDo.Api.Tests.Infrastructure;
using TimeToDo.Authentication.Domain.ExternalAuth;
using TimeToDo.Authentication.Domain.ExternalAuth.Events;
using TimeToDo.Authorization.Principals;
using TimeToDo.Authentication.Domain;


namespace TimeToDo.Api.Tests.ExternalAuth;

[Collection(IntegrationTestCollection.Name)]
public class ExternalIdentityLinkTests : IntegrationTestBase
{
    public ExternalIdentityLinkTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task LinkedEvent_MaterializesProjection()
    {
        var user = await Factory.CreateTestUserAsync("Alice", "Linker", "AL", "alice@acme.com");
        var linkId = Guid.NewGuid();
        var idpConfigId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<ExternalIdentityLink>(linkId,
                new ExternalIdentityLinkedEvent(
                    Id: linkId,
                    UserId: user.Id,
                    IdpConfigId: idpConfigId,
                    Issuer: "https://entra.example.com/tenant-1/v2.0",
                    Subject: "subject-abc-123",
                    Email: "alice@acme.com",
                    DisplayName: "Alice Linker",
                    LinkedAt: DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var link = await session.LoadAsync<ExternalIdentityLink>(linkId, TestContext.Current.CancellationToken);
            Assert.NotNull(link);
            Assert.Equal(user.Id, link!.UserId);
            Assert.Equal("subject-abc-123", link.Subject);
            Assert.False(link.IsUnlinked);
            // No ScriptRecorded event fired yet → script output is absent.
            // STJ round-trips null JsonDocument as an empty root, so we check
            // either C# null OR Undefined root-value-kind.
            Assert.True(
                link.LastScriptOutput is null
                || link.LastScriptOutput.RootElement.ValueKind == System.Text.Json.JsonValueKind.Undefined
                || link.LastScriptOutput.RootElement.ValueKind == System.Text.Json.JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task DuplicateIssuerSubject_ViolatesUniqueIndex()
    {
        var user1 = await Factory.CreateTestUserAsync("First", "User", "F1", "f1@test.com");
        var user2 = await Factory.CreateTestUserAsync("Second", "User", "S1", "s2@test.com");
        var idp = Guid.NewGuid();
        var issuer = "https://idp.test";
        var subject = "shared-subject-xyz";

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<ExternalIdentityLink>(Guid.NewGuid(),
                new ExternalIdentityLinkedEvent(
                    Id: Guid.NewGuid(), UserId: user1.Id, IdpConfigId: idp,
                    Issuer: issuer, Subject: subject,
                    Email: null, DisplayName: null, LinkedAt: DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<ExternalIdentityLink>(Guid.NewGuid(),
                new ExternalIdentityLinkedEvent(
                    Id: Guid.NewGuid(), UserId: user2.Id, IdpConfigId: idp,
                    Issuer: issuer, Subject: subject,
                    Email: null, DisplayName: null, LinkedAt: DateTimeOffset.UtcNow));

            // Unique-index on (Issuer, Subject) is enforced at the Postgres level
            // when the inline projection tries to insert the second document.
            await Assert.ThrowsAnyAsync<DocumentAlreadyExistsException>(
                async () => await session.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ScriptRecordedEvent_OverwritesLastScriptOutput()
    {
        var user = await Factory.CreateTestUserAsync("Bob", "Snap", "BS", "bob@acme.com");
        var linkId = Guid.NewGuid();
        var idpConfigId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<ExternalIdentityLink>(linkId,
                new ExternalIdentityLinkedEvent(
                    Id: linkId, UserId: user.Id, IdpConfigId: idpConfigId,
                    Issuer: "https://idp.test", Subject: "bob-subject",
                    Email: "bob@acme.com", DisplayName: "Bob", LinkedAt: DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var firstOutput = JsonDocument.Parse("""{"firstname":"Bob","email":"bob@acme.com"}""");
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(linkId,
                new ExternalIdentityScriptRecordedEvent(
                    Id: linkId, CapturedAt: DateTimeOffset.UtcNow,
                    ScriptSucceeded: true,
                    ScriptOutput: firstOutput,
                    ScriptError: null,
                    RawClaims: null,
                    Email: "bob@acme.com",
                    DisplayName: "Bob"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // A second login records a different script output — projection must overwrite.
        var secondOutput = JsonDocument.Parse("""{"firstname":"Robert","email":"bob@acme.com"}""");
        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(linkId,
                new ExternalIdentityScriptRecordedEvent(
                    Id: linkId, CapturedAt: DateTimeOffset.UtcNow.AddMinutes(5),
                    ScriptSucceeded: true,
                    ScriptOutput: secondOutput,
                    ScriptError: null,
                    RawClaims: null,
                    Email: null, DisplayName: null));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var link = await session.LoadAsync<ExternalIdentityLink>(linkId, TestContext.Current.CancellationToken);
            Assert.NotNull(link);
            Assert.NotNull(link!.LastScriptOutput);
            var json = link.LastScriptOutput!.RootElement.GetRawText();
            // Second snapshot wins — "Robert" replaced "Bob".
            Assert.Contains("Robert", json);
            Assert.DoesNotContain("\"Bob\"", json);
        }
    }

    [Fact]
    public async Task UserStreamMirrorEvent_AddsExternalIdentityRefToPrincipalDirectory()
    {
        var user = await Factory.CreateTestUserAsync("Carol", "Mirror", "CM", "carol@acme.com");
        var linkId = Guid.NewGuid();
        var idpConfigId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.Append(user.Id,
                new UserExternalIdentityLinkedEvent(
                    UserId: user.Id,
                    LinkId: linkId,
                    IdpConfigId: idpConfigId,
                    Issuer: "https://entra.example.com",
                    LinkedAt: DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var principal = await session.LoadAsync<Person>(user.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(principal);
            // principal is Person by type
            
            var refs = principal.ExternalIdentities;
            Assert.Single(refs);
            Assert.Equal(linkId, refs[0].LinkId);
            Assert.Equal(idpConfigId, refs[0].IdpConfigId);
            Assert.Equal("https://entra.example.com", refs[0].Issuer);
        }
    }

    [Fact]
    public async Task UnlinkEvent_RemovesRefFromPrincipalDirectory()
    {
        var user = await Factory.CreateTestUserAsync("Dave", "Unlinker", "DU", "dave@acme.com");
        var linkId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var idpConfigId = Guid.NewGuid();
            session.Events.Append(user.Id,
                new UserExternalIdentityLinkedEvent(user.Id, linkId, idpConfigId,
                    "https://idp.test", DateTimeOffset.UtcNow),
                new UserExternalIdentityUnlinkedEvent(user.Id, linkId, idpConfigId, DateTimeOffset.UtcNow.AddMinutes(1)));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var principal = await session.LoadAsync<Person>(user.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(principal);
            Assert.Empty(principal!.ExternalIdentities);
        }
    }
}
