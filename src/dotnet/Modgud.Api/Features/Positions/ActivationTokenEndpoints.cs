using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Helper;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Infrastructure.OpenIddict.Dpop;
using Modgud.Infrastructure.PositionTerminals;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Positions;

/// <summary>Lifecycle and RP-bound registration of position-owned WebAuthn
/// activation tokens (F2). Administration assigns the logical token; an
/// enrolled terminal performs attestation under its application's RP origin.</summary>
public static class ActivationTokenEndpoints
{
    public static WebApplication MapActivationTokenEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api")
            .WithTags("Position Activation Tokens")
            .RequireAuthorization();

        admin.MapGet("position/{positionId}/activation-tokens", ListAsync)
            .WithName("V2_PositionActivationTokens_List")
            .RequiresPermission("position:read");
        admin.MapPost("position/{positionId}/activation-tokens", CreateAsync)
            .WithName("V2_PositionActivationTokens_Create")
            .RequiresPermission("position:write");
        admin.MapPost("position/{positionId}/activation-tokens/{tokenId}/assign", AssignAsync)
            .WithName("V2_PositionActivationTokens_Assign")
            .RequiresPermission("position:write");
        admin.MapDelete("position/{positionId}/activation-tokens/{tokenId}", UnassignAsync)
            .WithName("V2_PositionActivationTokens_Unassign")
            .RequiresPermission("position:write");
        admin.MapPost("activation-token/{tokenId}/disable", DisableAsync)
            .WithName("V2_ActivationTokens_Disable")
            .RequiresPermission("position:write");
        admin.MapPost("activation-token/{tokenId}/reactivate", ReactivateAsync)
            .WithName("V2_ActivationTokens_Reactivate")
            .RequiresPermission("position:write");
        admin.MapPost("activation-token/{tokenId}/revoke", RevokeAsync)
            .WithName("V2_ActivationTokens_Revoke")
            .RequiresPermission("position:write");

        app.MapPost("/connect/activation-token/{tokenId}/register/begin", RegistrationBeginAsync)
            .WithName("ActivationToken_RegisterBegin")
            .WithTags("Position Staffing")
            .DisableAntiforgery()
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            })
            .RequireRateLimiting("passkey-begin");
        app.MapPost("/connect/activation-token/{tokenId}/register", RegistrationCompleteAsync)
            .WithName("ActivationToken_Register")
            .WithTags("Position Staffing")
            .DisableAntiforgery()
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            })
            .RequireRateLimiting("passkey-begin");

        return app;
    }

    private static async Task<IResult> ListAsync(
        ShortGuid positionId, AppSettings settings, IDocumentSession session, CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        if (await session.LoadAsync<PositionPrincipal>(positionId.Guid, ct) is not { IsDeleted: false })
            return Results.NotFound();
        var tokens = (await session.Query<ActivationToken>().ToListAsync(ct))
            .Where(t => t.AssignedPositionIds.Contains(positionId.Guid))
            .OrderBy(t => t.Label)
            .ToList();
        return Results.Ok(await ToDtosAsync(tokens, session, ct));
    }

    private static async Task<IResult> CreateAsync(
        ShortGuid positionId,
        ActivationTokenCreateDto dto,
        AppSettings settings,
        IDocumentSession session,
        HttpContext context,
        CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        if (await session.LoadAsync<PositionPrincipal>(positionId.Guid, ct) is not { IsDeleted: false })
            return Results.NotFound();
        var label = dto.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
            return Results.BadRequest(new { Error = "ActivationToken.LabelRequired", Message = "A label is required." });

        var token = new ActivationToken
        {
            Id = Guid.CreateVersion7(),
            Label = label,
            AssignedPositionIds = [positionId.Guid],
            CreatedByUserId = PositionGrantsEndpoints.RequireActor(context),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        session.Store(token);
        await session.SaveChangesAsync(ct);
        return Results.Ok((await ToDtosAsync([token], session, ct))[0]);
    }

    private static Task<IResult> AssignAsync(
        ShortGuid positionId, ShortGuid tokenId, AppSettings settings,
        IDocumentSession session, CancellationToken ct) =>
        ChangeAssignmentAsync(positionId.Guid, tokenId.Guid, assign: true, settings, session, null, ct);

    private static Task<IResult> UnassignAsync(
        ShortGuid positionId, ShortGuid tokenId, AppSettings settings,
        IDocumentSession session, IStaffingRevoker revoker, CancellationToken ct) =>
        ChangeAssignmentAsync(positionId.Guid, tokenId.Guid, assign: false, settings, session, revoker, ct);

    private static async Task<IResult> ChangeAssignmentAsync(
        Guid positionId, Guid tokenId, bool assign, AppSettings settings,
        IDocumentSession session, IStaffingRevoker? revoker, CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        if (await session.LoadAsync<PositionPrincipal>(positionId, ct) is not { IsDeleted: false })
            return Results.NotFound();
        var token = await session.LoadAsync<ActivationToken>(tokenId, ct);
        if (token is null || token.Status == ActivationTokenStatus.Revoked) return Results.NotFound();

        if (assign)
        {
            if (!token.AssignedPositionIds.Contains(positionId)) token.AssignedPositionIds.Add(positionId);
        }
        else
        {
            token.AssignedPositionIds.Remove(positionId);
        }
        session.Store(token);
        await session.SaveChangesAsync(ct);
        if (!assign && revoker is not null)
            await revoker.EndAllForActivationTokenAndPositionAsync(
                token.Id, positionId, StaffingSessionEndReason.ActivationTokenUnassigned, ct);
        return Results.Ok((await ToDtosAsync([token], session, ct))[0]);
    }

    private static Task<IResult> DisableAsync(
        ShortGuid tokenId, AppSettings settings, IDocumentSession session,
        IStaffingRevoker revoker, CancellationToken ct) =>
        ChangeStatusAsync(tokenId.Guid, ActivationTokenStatus.Disabled, settings, session, revoker, ct);

    private static Task<IResult> ReactivateAsync(
        ShortGuid tokenId, AppSettings settings, IDocumentSession session,
        IStaffingRevoker revoker, CancellationToken ct) =>
        ChangeStatusAsync(tokenId.Guid, ActivationTokenStatus.Active, settings, session, revoker, ct);

    private static async Task<IResult> RevokeAsync(
        ShortGuid tokenId, AppSettings settings, IDocumentSession session,
        IStaffingRevoker revoker, HttpContext context, CancellationToken ct)
    {
        // Keep the feature-off contract side-effect free. ChangeStatusAsync
        // also guards the flag, but RevokeAsync writes the actor/timestamp in
        // a second step and must not perform that write after a 404 result.
        if (!settings.Features.PositionTerminals) return Results.NotFound();

        var result = await ChangeStatusAsync(
            tokenId.Guid, ActivationTokenStatus.Revoked, settings, session, revoker, ct);
        if (await session.LoadAsync<ActivationToken>(tokenId.Guid, ct) is { } token && token.RevokedAt is null)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            token.RevokedByUserId = PositionGrantsEndpoints.RequireActor(context);
            session.Store(token);
            await session.SaveChangesAsync(ct);
        }
        return result;
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid tokenId, ActivationTokenStatus status, AppSettings settings,
        IDocumentSession session, IStaffingRevoker revoker, CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        var token = await session.LoadAsync<ActivationToken>(tokenId, ct);
        if (token is null) return Results.NotFound();
        if (token.Status == ActivationTokenStatus.Revoked && status != ActivationTokenStatus.Revoked)
            return Results.BadRequest(new { Error = "ActivationToken.Revoked", Message = "A revoked token cannot be reactivated." });
        if (status == ActivationTokenStatus.Active)
        {
            var hasCredential = await session.Query<ActivationTokenCredential>()
                .AnyAsync(c => c.ActivationTokenId == token.Id, ct);
            if (!hasCredential)
                return Results.BadRequest(new { Error = "ActivationToken.NotRegistered", Message = "Register a credential before reactivating the token." });
        }
        if (token.Status != status)
        {
            token.Status = status;
            session.Store(token);
            await session.SaveChangesAsync(ct);
            if (status is ActivationTokenStatus.Disabled or ActivationTokenStatus.Revoked)
                await revoker.EndAllForActivationTokenAsync(
                    token.Id, StaffingSessionEndReason.ActivationTokenRevoked, ct);
        }
        return Results.Ok((await ToDtosAsync([token], session, ct))[0]);
    }

    private static async Task<IResult> RegistrationBeginAsync(
        ShortGuid tokenId,
        HttpContext context,
        AppSettings settings,
        IDocumentSession session,
        RealmScopedFido2Factory fido2Factory,
        IDpopReplayStore replayStore,
        CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        var target = await ResolveTerminalAsync(context, session, replayStore, ct);
        if (target.Error is not null) return target.Error;
        var token = await session.LoadAsync<ActivationToken>(tokenId.Guid, ct);
        if (token is null || token.Status is ActivationTokenStatus.Revoked or ActivationTokenStatus.Disabled ||
            !token.AssignedPositionIds.Intersect(target.Terminal!.EffectiveAllowedPositionIds).Any())
            return Forbidden("ActivationToken.Unavailable", "The activation token is not assigned to this position.");

        IFido2 fido2;
        try { fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: target.Terminal!.WebAuthnRpId); }
        catch (RelyingPartyUnavailableException)
        {
            return Forbidden("ActivationToken.RelyingPartyUnavailable", "The terminal's relying party is unavailable.");
        }

        var existing = await session.Query<ActivationTokenCredential>()
            .Where(c => c.ActivationTokenId == token.Id && c.RpId == target.Terminal.WebAuthnRpId)
            .ToListAsync(ct);
        var fidoUser = new Fido2User
        {
            Id = Encoding.UTF8.GetBytes(token.Id.ToString()),
            Name = $"position-token-{new ShortGuid(token.Id)}",
            DisplayName = token.Label,
        };
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fidoUser,
            ExcludeCredentials = existing.Select(c => new PublicKeyCredentialDescriptor(c.CredentialId)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Discouraged,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        var now = DateTimeOffset.UtcNow;
        session.DeleteWhere<ActivationTokenRegistrationCeremony>(c => c.ExpiresAt < now);
        var ceremony = new ActivationTokenRegistrationCeremony
        {
            Id = Guid.NewGuid(),
            ActivationTokenId = token.Id,
            TerminalEnrollmentId = target.Terminal.Id,
            ClientId = target.Terminal.ClientId,
            RpId = target.Terminal.WebAuthnRpId,
            OptionsJson = options.ToJson(),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);
        return Results.Content(
            $"{{\"ceremonyId\":\"{ceremony.Id}\",\"options\":{ceremony.OptionsJson}}}", "application/json");
    }

    private static async Task<IResult> RegistrationCompleteAsync(
        ShortGuid tokenId,
        JsonElement body,
        HttpContext context,
        AppSettings settings,
        IDocumentSession session,
        RealmScopedFido2Factory fido2Factory,
        IDpopReplayStore replayStore,
        CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        var target = await ResolveTerminalAsync(context, session, replayStore, ct);
        if (target.Error is not null) return target.Error;
        if (!body.TryGetProperty("ceremonyId", out var idElement) ||
            !Guid.TryParse(idElement.GetString(), out var ceremonyId) ||
            !body.TryGetProperty("attestation", out var attestationElement))
            return Results.BadRequest(new { Error = "ActivationToken.InvalidRegistration", Message = "Invalid registration request." });

        var ceremony = await session.LoadAsync<ActivationTokenRegistrationCeremony>(ceremonyId, ct);
        if (ceremony is null || ceremony.IsExpired || ceremony.ActivationTokenId != tokenId.Guid ||
            ceremony.TerminalEnrollmentId != target.Terminal!.Id ||
            !string.Equals(ceremony.ClientId, target.Terminal.ClientId, StringComparison.Ordinal))
            return Results.BadRequest(new { Error = "ActivationToken.RegistrationExpired", Message = "Registration expired." });
        session.Delete(ceremony);
        await session.SaveChangesAsync(ct);

        var token = await session.LoadAsync<ActivationToken>(tokenId.Guid, ct);
        if (token is null || token.Status is ActivationTokenStatus.Revoked or ActivationTokenStatus.Disabled ||
            !token.AssignedPositionIds.Intersect(target.Terminal!.EffectiveAllowedPositionIds).Any())
            return Forbidden("ActivationToken.Unavailable", "The activation token is not assigned to this position.");

        AuthenticatorAttestationRawResponse? attestation;
        try
        {
            attestation = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
                attestationElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { attestation = null; }
        if (attestation is null)
            return Results.BadRequest(new { Error = "ActivationToken.AttestationFailed", Message = "Registration failed." });

        IFido2 fido2;
        try
        {
            var origin = RealmFido2.TryGetClientDataOrigin(attestation.Response?.ClientDataJson);
            fido2 = await fido2Factory.CreateAsync(
                ct, rpIdOverride: ceremony.RpId, additionalOrigins: origin is null ? null : [origin]);
        }
        catch (RelyingPartyUnavailableException)
        {
            return Results.BadRequest(new { Error = "ActivationToken.AttestationFailed", Message = "Registration failed." });
        }

        RegisteredPublicKeyCredential created;
        try
        {
            var options = CredentialCreateOptions.FromJson(ceremony.OptionsJson);
            created = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestation,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, innerCt) =>
                {
                    var positionCredentials = await session.Query<ActivationTokenCredential>().ToListAsync(innerCt);
                    var personCredentials = await session.Query<StoredPasskeyCredential>().ToListAsync(innerCt);
                    return !positionCredentials.Any(c => c.CredentialId.SequenceEqual(args.CredentialId)) &&
                           !personCredentials.Any(c => c.CredentialId.SequenceEqual(args.CredentialId));
                },
            }, ct);
        }
        catch
        {
            return Results.BadRequest(new { Error = "ActivationToken.AttestationFailed", Message = "Registration failed." });
        }

        var credential = new ActivationTokenCredential
        {
            Id = Guid.CreateVersion7(),
            ActivationTokenId = token.Id,
            CredentialId = created.Id,
            PublicKey = created.PublicKey,
            UserHandle = created.User.Id,
            SignatureCount = created.SignCount,
            AaGuid = created.AaGuid,
            RpId = ceremony.RpId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        token.Status = ActivationTokenStatus.Active;
        session.Store(credential);
        session.Store(token);
        await session.SaveChangesAsync(ct);
        return Results.Ok(new { CredentialId = new ShortGuid(credential.Id).ToString(), credential.RpId });
    }

    private static async Task<TerminalTarget> ResolveTerminalAsync(
        HttpContext context, IDocumentSession session, IDpopReplayStore replayStore, CancellationToken ct)
    {
        var principal = context.User;
        if (!string.Equals(principal.GetClaim(PositionTokenClaimTypes.TokenUse),
                PositionTokenUses.TerminalEnrollment, StringComparison.Ordinal) ||
            !principal.GetAudiences().Contains(PositionTerminalControl.Audience) ||
            !Guid.TryParse(principal.GetClaim(PositionTokenClaimTypes.TerminalId), out var terminalId))
            return new(null, null, Forbidden("ActivationToken.InvalidToken", "A terminal enrollment token is required."));

        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        PositionPrincipal? position = null;
        if (terminal is not null)
        {
            foreach (var positionId in terminal.EffectiveAllowedPositionIds)
            {
                var candidate = await session.LoadAsync<PositionPrincipal>(positionId, ct);
                if (candidate is { IsDeleted: false, IsActive: true } && candidate.TerminalPolicy.Enabled)
                {
                    position = candidate;
                    break;
                }
            }
        }
        if (terminal is null || position is null || position.IsDeleted ||
            terminal.Status != TerminalEnrollmentStatus.Active || !position.TerminalPolicy.Enabled)
            return new(null, null, Forbidden("ActivationToken.TerminalUnavailable", "The terminal is not active."));

        if (string.Equals(terminal.Binding, DeviceBindingIds.Dpop, StringComparison.Ordinal))
        {
            var header = context.Request.Headers[DpopConstants.HeaderName];
            var authorization = context.Request.Headers.Authorization.ToString();
            var separator = authorization.IndexOf(' ');
            if (header.Count != 1 || separator <= 0 || separator == authorization.Length - 1)
                return new(null, null, Forbidden("ActivationToken.DpopRequired", "A DPoP proof is required."));
            var accessToken = authorization[(separator + 1)..].Trim();
            var now = DateTimeOffset.UtcNow;
            var htu = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
            var proof = DpopProofValidator.Validate(header.ToString(), context.Request.Method, htu, now, accessToken);
            if (!proof.IsValid || !string.Equals(proof.Jkt, terminal.DpopJkt, StringComparison.Ordinal))
                return new(null, null, Forbidden("ActivationToken.DpopMismatch", "The DPoP proof key does not match this terminal."));
            var expires = proof.IssuedAt!.Value + DpopProofValidator.DefaultMaxAge + DpopProofValidator.DefaultClockSkew;
            if (!await replayStore.TryRecordAsync(proof.Jti!, expires, now, ct))
                return new(null, null, Forbidden("ActivationToken.DpopReplay", "The DPoP proof has already been used."));
        }
        return new(terminal, position, null);
    }

    private static async Task<List<ActivationTokenDto>> ToDtosAsync(
        IReadOnlyList<ActivationToken> tokens, IDocumentSession session, CancellationToken ct)
    {
        var credentials = await session.Query<ActivationTokenCredential>().ToListAsync(ct);
        return tokens.Select(t => new ActivationTokenDto
        {
            Id = new ShortGuid(t.Id).ToString(),
            Label = t.Label,
            Status = t.Status,
            AssignedPositionIds = t.AssignedPositionIds.Select(ShortGuid.Encode).ToArray(),
            RegisteredRpIds = credentials.Where(c => c.ActivationTokenId == t.Id)
                .Select(c => c.RpId).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            CreatedAt = t.CreatedAt,
        }).ToList();
    }

    private static IResult Forbidden(string error, string message) =>
        Results.Json(new { Error = error, Message = message }, statusCode: StatusCodes.Status403Forbidden);

    private sealed record TerminalTarget(TerminalEnrollment? Terminal, PositionPrincipal? Position, IResult? Error);
}

public sealed class ActivationTokenCreateDto
{
    public string? Label { get; set; }
}

public sealed class ActivationTokenDto
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public ActivationTokenStatus Status { get; init; }
    public IReadOnlyList<string> AssignedPositionIds { get; init; } = [];
    public IReadOnlyList<string> RegisteredRpIds { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
}
