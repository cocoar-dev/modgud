using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Modgud.Authentication.Domain;

namespace Modgud.Authentication.Identity;

/// <summary>
/// The single, shared WebAuthn assertion verifier used by BOTH the web cookie
/// passkey login (<c>PasskeyEndpoints</c>) and the native (ADR-0010 Phase 2)
/// <c>urn:cocoar:passkey</c> grant — one FIDO2 verify implementation, no fork.
/// Only the CHALLENGE TRANSPORT differs between the two callers (web: an HttpOnly
/// cookie; native: the server-side <see cref="PasskeyCeremony"/> doc); both pass
/// the rehydrated <see cref="AssertionOptions"/> in.
/// </summary>
public static class PasskeyAssertionVerifier
{
    /// <summary>
    /// Verifies a WebAuthn assertion against a previously-issued
    /// <paramref name="originalOptions"/>, resolves the matching
    /// <see cref="StoredPasskeyCredential"/> (by credential id, tenant-scoped via
    /// <paramref name="session"/>), and advances + persists its signature counter.
    /// Returns the matched credential (the caller loads the user from
    /// <see cref="StoredPasskeyCredential.UserId"/>) or <c>null</c> on ANY failure
    /// — fails closed (bad JSON, unknown credential, signature/origin/counter
    /// mismatch all yield null, never an exception to the caller).
    /// </summary>
    public static async Task<StoredPasskeyCredential?> VerifyAsync(
        IFido2 fido2,
        AssertionOptions originalOptions,
        string assertionJson,
        IDocumentSession session,
        CancellationToken ct = default)
    {
        AuthenticatorAssertionRawResponse? assertionResponse;
        try
        {
            assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                assertionJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        // Response is not a keyword-required member, so a body like
        // {"id":"…","type":"public-key"} deserializes with a null Response and
        // would NRE inside MakeAssertionAsync — reject it here (fail closed).
        if (assertionResponse is null || string.IsNullOrEmpty(assertionResponse.Id) || assertionResponse.Response is null)
            return null;

        // Decode the base64url credential id the authenticator returned.
        byte[] assertionCredentialId;
        try
        {
            assertionCredentialId = Convert.FromBase64String(
                assertionResponse.Id.Replace('-', '+').Replace('_', '/').PadRight(
                    assertionResponse.Id.Length + (4 - assertionResponse.Id.Length % 4) % 4, '='));
        }
        catch (FormatException)
        {
            return null;
        }

        // CredentialId is a byte[] and is NOT DB-indexed (Marten can't translate
        // SequenceEqual on byte[]) — load the tenant's credentials and match in
        // memory. Discoverable/usernameless login resolves the user from the
        // credential the authenticator picked, so the lookup is by credential id.
        var allCredentials = await session.Query<StoredPasskeyCredential>().ToListAsync(ct);
        var storedCredential = allCredentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(assertionCredentialId));
        if (storedCredential is null)
            return null;

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = originalOptions,
                StoredPublicKey = storedCredential.PublicKey,
                StoredSignatureCounter = storedCredential.SignatureCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                {
                    var credential = allCredentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(args.CredentialId));
                    return Task.FromResult(credential?.UserHandle.SequenceEqual(args.UserHandle) ?? false);
                },
            }, ct);
        }
        catch
        {
            // ANY throw out of the verify path is a rejected proof, not a
            // recoverable server fault: a Fido2VerificationException (signature /
            // origin / RP-ID / counter mismatch) OR a raw parser exception from
            // malformed authenticator data (e.g. System.Formats.Cbor on a crafted
            // extension-data flag). Fail closed so both callers stay uniform — the
            // web endpoint returns 401 and the native grant invalid_grant, never a
            // 500 (honours this method's documented contract).
            return null;
        }

        // Advance the clone-detection signature counter (kept consistent across
        // the web + native paths since both go through this single verifier).
        storedCredential.SignatureCount = result.SignCount;
        storedCredential.LastUsedAt = DateTimeOffset.UtcNow;
        session.Store(storedCredential);
        await session.SaveChangesAsync(ct);

        return storedCredential;
    }
}
