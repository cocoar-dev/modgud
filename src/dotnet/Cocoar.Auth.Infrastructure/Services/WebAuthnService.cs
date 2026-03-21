using System.Text;
using System.Text.Json;
using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using ErrorOr;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Service for managing WebAuthn/FIDO2 authentication.
/// </summary>
public class WebAuthnService : IWebAuthnService
{
    private readonly IFido2 _fido2;
    private readonly IDocumentSession _session;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<WebAuthnService> _logger;

    public WebAuthnService(
        IFido2 fido2,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        ILogger<WebAuthnService> logger)
    {
        _fido2 = fido2;
        _session = session;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<ErrorOr<WebAuthnRegistrationOptionsDto>> GetRegistrationOptionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        // Get existing credentials to exclude
        var securityData = await _session.LoadAsync<UserSecurityData>(userId, cancellationToken);
        var existingCredentials = securityData?.WebAuthnCredentials
            .Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId)))
            .ToList() ?? [];

        // Create Fido2 user
        var fido2User = new Fido2User
        {
            Id = userId.ToByteArray(),
            Name = user.UserName ?? user.Email ?? userId.ToString(),
            DisplayName = GetDisplayName(user)
        };

        // Create registration options
        var options = _fido2.RequestNewCredential(
            new RequestNewCredentialParams
            {
                User = fido2User,
                ExcludeCredentials = existingCredentials,
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Preferred,
                    UserVerification = UserVerificationRequirement.Preferred
                },
                AttestationPreference = AttestationConveyancePreference.None
            });

        // Store challenge for verification
        var challenge = WebAuthnChallenge.Create(
            userId,
            Convert.ToBase64String(options.Challenge),
            WebAuthnChallenge.TypeRegistration,
            JsonSerializer.Serialize(options));

        _session.Store(challenge);
        await _session.SaveChangesAsync(cancellationToken);

        return new WebAuthnRegistrationOptionsDto
        {
            Options = JsonSerializer.SerializeToElement(options)
        };
    }

    public async Task<ErrorOr<WebAuthnRegistrationResultDto>> CompleteRegistrationAsync(
        Guid userId,
        JsonElement attestationResponse,
        string? deviceName,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        // Find the challenge
        var challenges = await _session.Query<WebAuthnChallenge>()
            .Where(c => c.UserId == userId && c.Type == WebAuthnChallenge.TypeRegistration)
            .ToListAsync(cancellationToken);

        var challenge = challenges.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
        if (challenge is null || challenge.IsExpired || string.IsNullOrEmpty(challenge.OptionsJson))
        {
            return WebAuthnErrors.InvalidChallenge;
        }

        try
        {
            // Deserialize the original options
            var originalOptions = JsonSerializer.Deserialize<CredentialCreateOptions>(challenge.OptionsJson);
            if (originalOptions is null)
            {
                return WebAuthnErrors.InvalidChallenge;
            }

            // Parse the attestation response
            var attestationResponseJson = attestationResponse.GetRawText();
            var attestationResponseObj = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationResponseJson);
            if (attestationResponseObj is null)
            {
                return WebAuthnErrors.AttestationFailed;
            }

            // Verify the attestation
            var registeredCredential = await _fido2.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = attestationResponseObj,
                    OriginalOptions = originalOptions,
                    IsCredentialIdUniqueToUserCallback = IsCredentialIdUniqueToUserAsync
                },
                cancellationToken);

            // Create credential entity
            var credential = new WebAuthnCredential
            {
                CredentialId = Convert.ToBase64String(registeredCredential.Id),
                PublicKey = registeredCredential.PublicKey,
                UserHandle = registeredCredential.User.Id,
                SignCount = registeredCredential.SignCount,
                DeviceName = deviceName ?? "Security Key",
                AuthenticatorType = registeredCredential.AttestationFormat,
                Aaguid = registeredCredential.AaGuid,
                CreatedAt = DateTimeOffset.UtcNow,
                Transports = registeredCredential.Transports?.Select(t => t.ToString()).ToArray()
            };

            // Store credential in UserSecurityData
            var securityData = await _session.LoadAsync<UserSecurityData>(userId, cancellationToken);
            if (securityData is null)
            {
                securityData = UserSecurityData.Create(userId);
            }

            // Check for duplicate
            if (securityData.WebAuthnCredentials.Any(c => c.CredentialId == credential.CredentialId))
            {
                return WebAuthnErrors.CredentialAlreadyRegistered;
            }

            securityData.WebAuthnCredentials.Add(credential);
            _session.Store(securityData);

            // Delete the used challenge
            _session.Delete(challenge);

            // Record event
            _session.Events.Append(userId, new WebAuthnCredentialRegistered(
                userId,
                credential.CredentialId,
                deviceName));

            await _session.SaveChangesAsync(cancellationToken);

            return new WebAuthnRegistrationResultDto
            {
                CredentialId = credential.CredentialId,
                DeviceName = credential.DeviceName,
                CreatedAt = credential.CreatedAt
            };
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogWarning(ex, "WebAuthn registration verification failed");
            return WebAuthnErrors.AttestationFailed;
        }
    }

    public async Task<ErrorOr<WebAuthnAuthenticationOptionsDto>> GetAuthenticationOptionsAsync(
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        List<PublicKeyCredentialDescriptor> allowedCredentials = [];

        if (userId.HasValue)
        {
            var securityData = await _session.LoadAsync<UserSecurityData>(userId.Value, cancellationToken);
            if (securityData?.WebAuthnCredentials.Count > 0)
            {
                allowedCredentials = securityData.WebAuthnCredentials
                    .Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId)))
                    .ToList();
            }
            else
            {
                return WebAuthnErrors.NoCredentialsRegistered;
            }
        }

        // Create authentication options
        var options = _fido2.GetAssertionOptions(
            new GetAssertionOptionsParams
            {
                AllowedCredentials = allowedCredentials,
                UserVerification = UserVerificationRequirement.Preferred
            });

        // Store challenge for verification
        var effectiveUserId = userId ?? Guid.Empty;
        var challenge = WebAuthnChallenge.Create(
            effectiveUserId,
            Convert.ToBase64String(options.Challenge),
            WebAuthnChallenge.TypeAuthentication,
            JsonSerializer.Serialize(options));

        _session.Store(challenge);
        await _session.SaveChangesAsync(cancellationToken);

        return new WebAuthnAuthenticationOptionsDto
        {
            Options = JsonSerializer.SerializeToElement(options)
        };
    }

    public async Task<ErrorOr<Guid>> VerifyAuthenticationAsync(
        Guid? userId,
        JsonElement assertionResponse,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse the assertion response
            var assertionResponseJson = assertionResponse.GetRawText();
            var assertionResponseObj = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionResponseJson);
            if (assertionResponseObj?.Response is null || assertionResponseObj.Id is null)
            {
                return WebAuthnErrors.AssertionFailed;
            }

            // For passwordless flow, try to get user ID from userHandle
            var effectiveUserId = userId;
            if (!effectiveUserId.HasValue && assertionResponseObj.Response.UserHandle?.Length > 0)
            {
                try
                {
                    effectiveUserId = new Guid(assertionResponseObj.Response.UserHandle);
                }
                catch
                {
                    // UserHandle is not a valid GUID, continue with null userId
                }
            }

            // Find the credential (Id is base64url encoded in Fido2 v4)
            var credentialId = assertionResponseObj.Id;
            var (ownerUserId, credential, securityData) = await FindCredentialAsync(credentialId, effectiveUserId, cancellationToken);

            if (credential is null || securityData is null)
            {
                return WebAuthnErrors.CredentialNotFound;
            }

            // Find the challenge
            var challengeUserId = userId ?? Guid.Empty;
            var challenges = await _session.Query<WebAuthnChallenge>()
                .Where(c => c.UserId == challengeUserId && c.Type == WebAuthnChallenge.TypeAuthentication)
                .ToListAsync(cancellationToken);

            var challenge = challenges.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
            if (challenge is null || challenge.IsExpired || string.IsNullOrEmpty(challenge.OptionsJson))
            {
                return WebAuthnErrors.InvalidChallenge;
            }

            // Deserialize the original options
            var originalOptions = JsonSerializer.Deserialize<AssertionOptions>(challenge.OptionsJson);
            if (originalOptions is null)
            {
                return WebAuthnErrors.InvalidChallenge;
            }

            // Verify the assertion
            var assertionResult = await _fido2.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = assertionResponseObj,
                    OriginalOptions = originalOptions,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = credential.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = IsUserHandleOwnerOfCredentialIdAsync
                },
                cancellationToken);

            // Check for sign count anomaly (potential cloned authenticator)
            if (assertionResult.SignCount < credential.SignCount && credential.SignCount != 0)
            {
                _logger.LogWarning(
                    "WebAuthn sign count mismatch for credential {CredentialId}: stored={Stored}, received={Received}",
                    credentialId, credential.SignCount, assertionResult.SignCount);
                return WebAuthnErrors.SignCountMismatch;
            }

            // Update the credential
            credential.SignCount = assertionResult.SignCount;
            credential.LastUsedAt = DateTimeOffset.UtcNow;
            _session.Store(securityData);

            // Delete the used challenge
            _session.Delete(challenge);

            // Record event
            _session.Events.Append(ownerUserId, new WebAuthnCredentialUsed(
                ownerUserId,
                credentialId,
                ipAddress));

            await _session.SaveChangesAsync(cancellationToken);

            return ownerUserId;
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogWarning(ex, "WebAuthn assertion verification failed");
            return WebAuthnErrors.AssertionFailed;
        }
    }

    public async Task<ErrorOr<WebAuthnCredentialListDto>> GetCredentialsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var securityData = await _session.LoadAsync<UserSecurityData>(userId, cancellationToken);

        var credentials = securityData?.WebAuthnCredentials
            .Select(c => new WebAuthnCredentialDto
            {
                Id = c.CredentialId,
                DeviceName = c.DeviceName,
                AuthenticatorType = c.AuthenticatorType,
                CreatedAt = c.CreatedAt,
                LastUsedAt = c.LastUsedAt
            })
            .ToList() ?? [];

        return new WebAuthnCredentialListDto
        {
            Credentials = credentials
        };
    }

    public async Task<ErrorOr<bool>> DeleteCredentialAsync(
        Guid userId,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        var securityData = await _session.LoadAsync<UserSecurityData>(userId, cancellationToken);
        if (securityData is null)
        {
            return WebAuthnErrors.CredentialNotFound;
        }

        var credential = securityData.WebAuthnCredentials.FirstOrDefault(c => c.CredentialId == credentialId);
        if (credential is null)
        {
            return WebAuthnErrors.CredentialNotFound;
        }

        securityData.WebAuthnCredentials.Remove(credential);
        _session.Store(securityData);

        // Record event
        _session.Events.Append(userId, new WebAuthnCredentialDeleted(userId, credentialId));

        await _session.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<ErrorOr<bool>> RenameCredentialAsync(
        Guid userId,
        string credentialId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var securityData = await _session.LoadAsync<UserSecurityData>(userId, cancellationToken);
        if (securityData is null)
        {
            return WebAuthnErrors.CredentialNotFound;
        }

        var credential = securityData.WebAuthnCredentials.FirstOrDefault(c => c.CredentialId == credentialId);
        if (credential is null)
        {
            return WebAuthnErrors.CredentialNotFound;
        }

        credential.DeviceName = name;
        _session.Store(securityData);
        await _session.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<int> GetCredentialCountAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var securityData = await _session.LoadAsync<UserSecurityData>(userId, cancellationToken);
        return securityData?.WebAuthnCredentials.Count ?? 0;
    }

    private async Task<bool> IsCredentialIdUniqueToUserAsync(
        IsCredentialIdUniqueToUserParams credentialIdUserParams,
        CancellationToken cancellationToken)
    {
        var credentialId = Convert.ToBase64String(credentialIdUserParams.CredentialId);

        // Query all UserSecurityData documents to check for duplicate credential IDs
        var existingData = await _session.Query<UserSecurityData>()
            .Where(s => s.WebAuthnCredentials.Any(c => c.CredentialId == credentialId))
            .FirstOrDefaultAsync(cancellationToken);

        return existingData is null;
    }

    private async Task<bool> IsUserHandleOwnerOfCredentialIdAsync(
        IsUserHandleOwnerOfCredentialIdParams userHandleParams,
        CancellationToken cancellationToken)
    {
        var credentialId = Convert.ToBase64String(userHandleParams.CredentialId);
        var userHandle = userHandleParams.UserHandle;

        // Find the credential and verify the user handle matches
        var userId = new Guid(userHandle);
        var securityData = await _session.LoadAsync<UserSecurityData>(userId, cancellationToken);

        if (securityData is null)
        {
            return false;
        }

        return securityData.WebAuthnCredentials.Any(c => c.CredentialId == credentialId);
    }

    private async Task<(Guid UserId, WebAuthnCredential? Credential, UserSecurityData? SecurityData)> FindCredentialAsync(
        string credentialId,
        Guid? expectedUserId,
        CancellationToken cancellationToken)
    {
        // Normalize credential ID to standard base64 for comparison
        // Fido2 v4 returns base64url encoded IDs, but we store them as standard base64
        var normalizedCredentialId = NormalizeBase64(credentialId);

        if (expectedUserId.HasValue)
        {
            var securityData = await _session.LoadAsync<UserSecurityData>(expectedUserId.Value, cancellationToken);
            var credential = securityData?.WebAuthnCredentials.FirstOrDefault(c =>
                NormalizeBase64(c.CredentialId) == normalizedCredentialId);
            return (expectedUserId.Value, credential, securityData);
        }

        // Passwordless flow - search all users (can't use LINQ query with normalization, so fetch and filter)
        var allSecurityData = await _session.Query<UserSecurityData>()
            .Where(s => s.WebAuthnCredentials.Count > 0)
            .ToListAsync(cancellationToken);

        foreach (var securityData in allSecurityData)
        {
            var credential = securityData.WebAuthnCredentials.FirstOrDefault(c =>
                NormalizeBase64(c.CredentialId) == normalizedCredentialId);
            if (credential is not null)
            {
                return (securityData.Id, credential, securityData);
            }
        }

        return (Guid.Empty, null, null);
    }

    /// <summary>
    /// Normalizes a base64 or base64url string to standard base64 for comparison.
    /// </summary>
    private static string NormalizeBase64(string input)
    {
        // Convert base64url to base64
        var base64 = input.Replace('-', '+').Replace('_', '/');
        // Add padding if needed
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return base64;
    }

    private static string GetDisplayName(ApplicationUser user)
    {
        if (!string.IsNullOrEmpty(user.FirstName) || !string.IsNullOrEmpty(user.LastName))
        {
            return $"{user.FirstName} {user.LastName}".Trim();
        }
        return user.UserName ?? user.Email ?? "User";
    }
}
