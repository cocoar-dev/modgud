using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Marten;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Wave 6 of the "similar bugs" remediation — passkey hardening (#33, #16).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditWave6Tests : IntegrationTestBase
{
    public SecurityAuditWave6Tests(SharedPostgresFixture fixture) : base(fixture) { }

    // #33 — the enroll uniqueness guard must compare CredentialId (byte[]) IN MEMORY.
    // A Marten LINQ `c.CredentialId == bytes` is untranslatable (Postgres 22P02), so
    // the buggy guard threw on every web enroll (500) and the uniqueness check was
    // effectively skipped. Asserts the in-memory SequenceEqual approach detects a
    // duplicate + a distinct id, and pins that the byte[]== LINQ form still throws.
    [Fact]
    public async Task PasskeyEnroll_CredentialIdUniqueness_ComparesInMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        var credentialId = new byte[] { 1, 2, 3, 4, 5 };
        await using (var seed = GetTenantedDocumentSession())
        {
            seed.Store(new StoredPasskeyCredential
            {
                Id = Guid.CreateVersion7(),
                UserId = Guid.NewGuid(),
                CredentialId = credentialId,
                PublicKey = [9, 9],
                UserHandle = [8, 8],
                DisplayName = "Passkey",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync(ct);
        }

        await using var session = GetTenantedDocumentSession();

        // The fix: materialize, then SequenceEqual (mirrors the enroll callback).
        var all = await session.Query<StoredPasskeyCredential>().ToListAsync(ct);
        Assert.Contains(all, c => c.CredentialId.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.DoesNotContain(all, c => c.CredentialId.SequenceEqual(new byte[] { 7, 7 }));

        // The bug: the byte[]== LINQ form is untranslatable and throws at query time.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await session.Query<StoredPasskeyCredential>()
                .AnyAsync(c => c.CredentialId == credentialId, ct));
    }

    // #16 — passkey login options must REQUIRE user verification, so a passkey login
    // is genuinely MFA-grade rather than possession-only. RED before Wave 6: the
    // options requested UserVerification "preferred", which the authenticator may
    // skip. Asserts the generated assertion options carry "required".
    [Fact]
    public async Task PasskeyLoginOptions_RequireUserVerification()
    {
        var ct = TestContext.Current.CancellationToken;
        var anon = Factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/api/account/passkey/login-options", new { }, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var uv = doc.RootElement.GetProperty("userVerification").GetString();
        Assert.Equal("required", uv);
    }
}
