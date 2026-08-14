using System.Text.RegularExpressions;
using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.ServiceAccount;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.Services;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Domain.ValueObjects;
using Modgud.Domain.OAuth.Common;
using Modgud.Infrastructure.OpenIddict;
using Marten;

namespace Modgud.Api.Features.ServiceAccounts;

/// <summary>
/// Admin CRUD for <see cref="ServiceAccount"/> principals — the non-human leg
/// of the Principal hierarchy. Service accounts carry an account-name for
/// audit/log correlation and a free-text Purpose; they don't have email,
/// password, or MFA. AccountName uniqueness is checked across the whole
/// principal table (Person + ServiceAccount) because both can act as login
/// identifiers downstream.
/// </summary>
public static class ServiceAccountsEndpoints
{
    private static readonly Regex AccountNamePattern =
        new("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.Compiled);

    public static WebApplication MapServiceAccountsEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/service-account")
            .WithTags("Service Accounts")
            .RequireAuthorization();

        group.MapGet("", async (IDocumentSession session) =>
            {
                var rows = await session.Query<ServiceAccount>()
                    .Where(s => !s.IsDeleted)
                    .OrderBy(s => s.AccountName)
                    .ToListAsync();

                return Results.Ok(rows.Select(ToDto));
            })
            .WithName("V2_ServiceAccount_GetAll")
            .RequiresPermission("service-account:read");

        group.MapGet("{id}", async (ShortGuid id, IDocumentSession session) =>
            {
                var sa = await session.LoadAsync<ServiceAccount>(id.Guid);
                if (sa is null || sa.IsDeleted) return Results.NotFound();
                return Results.Ok(ToDto(sa));
            })
            .WithName("V2_ServiceAccount_GetById")
            .RequiresPermission("service-account:read");

        group.MapPost("", async (
                ServiceAccountCreateDto dto,
                IDocumentSession session,
                OAuthAdminService oauth,
                DataEventDispatcher dispatcher,
                CancellationToken ct) =>
            {
                var normalised = (dto.AccountName ?? string.Empty).Trim().ToLowerInvariant();
                var validation = ValidateAccountName(normalised);
                if (validation is not null) return validation;

                // Cross-Principal uniqueness — Person.AccountName and
                // ServiceAccount.AccountName can both end up as the `sub` /
                // login handle, so they share a namespace.
                if (await session.Query<Person>().AnyAsync(p => !p.IsDeleted && p.AccountName == normalised))
                    return Results.Conflict(new { Error = "ServiceAccount.AccountNameTaken",
                        Message = $"Account name '{normalised}' is already used by a person." });

                if (await session.Query<ServiceAccount>().AnyAsync(s => !s.IsDeleted && s.AccountName == normalised))
                    return Results.Conflict(new { Error = "ServiceAccount.AccountNameTaken",
                        Message = $"Account name '{normalised}' is already in use." });

                // MG-FT-01 — function principals joined the shared namespace.
                if (await session.Query<FunctionPrincipal>().AnyAsync(f => !f.IsDeleted && f.AccountName == normalised))
                    return Results.Conflict(new { Error = "ServiceAccount.AccountNameTaken",
                        Message = $"Account name '{normalised}' is already used by a function." });

                // When an initial credential is supplied, delegate to the OAuth
                // create path that already supports inline ServiceAccount
                // creation. It stages principal, OAuth stream and hashed secret
                // in the same Marten session and commits them atomically.
                if (dto.InitialCredential is { } initialCredential)
                {
                    var clientId = string.IsNullOrWhiteSpace(initialCredential.ClientId)
                        ? $"{normalised}.{new ShortGuid(Guid.NewGuid()).ToString()[..8]}"
                        : initialCredential.ClientId.Trim();
                    var result = await oauth.CreateClientAsync(new CreateOAuthClientDto
                    {
                        ClientId = clientId,
                        DisplayName = string.IsNullOrWhiteSpace(initialCredential.DisplayName)
                            ? normalised
                            : initialCredential.DisplayName.Trim(),
                        ClientType = OAuthClientTypes.Confidential,
                        ConsentType = OAuthConsentTypes.Implicit,
                        AllowedGrantTypes = ["client_credentials"],
                        Scopes = initialCredential.Scopes,
                        RequireClientSecret = true,
                        RequireConsent = false,
                        Enabled = initialCredential.Enabled,
                        AccessTokenType = initialCredential.AccessTokenType,
                        AccessTokenLifetime = initialCredential.AccessTokenLifetime,
                        AppIds = initialCredential.AppIds,
                        NewServiceAccount = new ServiceAccountCreateDto
                        {
                            AccountName = normalised,
                            Purpose = dto.Purpose,
                            IsActive = dto.IsActive,
                        },
                    }, ct);
                    if (result.IsError) return result.ToResult();

                    var createdWithCredential = result.Value.CreatedServiceAccount!;
                    createdWithCredential.InitialCredential = new ServiceAccountCredentialIssuedDto
                    {
                        Credential = result.Value.Client,
                        ClientSecret = result.Value.ClientSecret!,
                    };
                    // Keep both admin grids in sync: this endpoint created both
                    // aggregate types even though the response is SA-shaped.
                    dispatcher.DispatchCreatedEvent("OAuthClient", result.Value.Client, session.TenantId);
                    dispatcher.DispatchCreatedEvent("ServiceAccount", createdWithCredential, session.TenantId);
                    return Results.Ok(createdWithCredential);
                }

                var sa = new ServiceAccount
                {
                    Id = Guid.NewGuid(),
                    AccountName = normalised,
                    Purpose = string.IsNullOrWhiteSpace(dto.Purpose) ? null : dto.Purpose.Trim(),
                    IsActive = dto.IsActive,
                };
                session.Store(sa);
                await session.SaveChangesAsync(ct);

                var created = ToDto(sa);
                dispatcher.DispatchCreatedEvent("ServiceAccount", created, session.TenantId);
                return Results.Ok(created);
            })
            .WithName("V2_ServiceAccount_Create")
            .RequiresPermission("service-account:write");

        group.MapPut("{id}", async (ShortGuid id, ServiceAccountUpdateDto dto, IDocumentSession session, DataEventDispatcher dispatcher, IOAuthGrantRevoker revoker, CancellationToken ct) =>
            {
                var sa = await session.LoadAsync<ServiceAccount>(id.Guid, ct);
                if (sa is null || sa.IsDeleted) return Results.NotFound();

                // Prior active-state, read from the persisted record (not the request).
                var wasActive = sa.IsActive;

                if (dto.AccountName is { } rawAccountName)
                {
                    var normalised = rawAccountName.Trim().ToLowerInvariant();
                    if (normalised != sa.AccountName)
                    {
                        var validation = ValidateAccountName(normalised);
                        if (validation is not null) return validation;

                        var personTaken = await session.Query<Person>()
                            .AnyAsync(p => !p.IsDeleted && p.AccountName == normalised);
                        if (personTaken)
                            return Results.Conflict(new { Error = "ServiceAccount.AccountNameTaken",
                                Message = $"Account name '{normalised}' is already used by a person." });

                        var saTaken = await session.Query<ServiceAccount>()
                            .AnyAsync(s => !s.IsDeleted && s.Id != id.Guid && s.AccountName == normalised);
                        if (saTaken)
                            return Results.Conflict(new { Error = "ServiceAccount.AccountNameTaken",
                                Message = $"Account name '{normalised}' is already in use." });

                        // MG-FT-01 — function principals joined the shared namespace.
                        var functionTaken = await session.Query<FunctionPrincipal>()
                            .AnyAsync(f => !f.IsDeleted && f.AccountName == normalised);
                        if (functionTaken)
                            return Results.Conflict(new { Error = "ServiceAccount.AccountNameTaken",
                                Message = $"Account name '{normalised}' is already used by a function." });

                        sa.AccountName = normalised;
                    }
                }

                if (dto.Purpose is not null)
                    sa.Purpose = string.IsNullOrWhiteSpace(dto.Purpose) ? null : dto.Purpose.Trim();

                if (dto.IsActive.HasValue)
                    sa.IsActive = dto.IsActive.Value;

                session.Store(sa);
                await session.SaveChangesAsync(ct);

                // Audit #6 — deactivating an SA must cut off its live M2M access, not
                // just block new token issuance. The SA's client_credentials tokens
                // carry sub = sa.Id (across every credential), so a by-subject revoke
                // kills them all; reactivation re-issues normally. Gate on the
                // persisted active→inactive TRANSITION (prior state from the load,
                // new state read back from the store) rather than the raw request
                // flag: a benign edit, or re-saving an already-inactive SA, does not
                // trigger a pointless revoke sweep.
                if (wasActive)
                {
                    var persisted = await session.LoadAsync<ServiceAccount>(id.Guid, ct);
                    if (persisted is { IsActive: false })
                    {
                        var subject = persisted.Id.ToString();
                        await revoker.RevokeTokensBySubjectAsync(subject, ct);
                        await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);
                    }
                }

                var updated = ToDto(sa);
                dispatcher.DispatchUpdatedEvent("ServiceAccount", updated, session.TenantId);
                return Results.Ok(updated);
            })
            .WithName("V2_ServiceAccount_Update")
            .RequiresPermission("service-account:write");

        group.MapDelete("{id}", async (
                ShortGuid id,
                IDocumentSession session,
                OAuthAdminService oauth,
                DataEventDispatcher dispatcher,
                IOAuthGrantRevoker revoker,
                CancellationToken ct) =>
            {
                var sa = await session.LoadAsync<ServiceAccount>(id.Guid, ct);
                if (sa is null || sa.IsDeleted) return Results.NotFound();

                // Phase 2C — cascade-delete every credential owned by this SA
                // PLUS soft-delete the SA itself in one unit of work. The SA
                // delete is queued via the strongly-typed Delete<T> overload
                // (Marten translates this into a soft-delete on the polymorphic
                // mt_doc_principal table without triggering an optimistic
                // concurrency check against the in-memory ServiceAccount
                // instance we loaded above — which Store(sa) would have).
                var deletedCredentialCount = await oauth
                    .StageDeleteAllServiceAccountCredentialsAsync(id.Guid, ct);

                // We want soft-delete semantics (IsDeleted=true) so audit /
                // group-membership references stay resolvable. Mutate the
                // loaded instance + Update — Marten's Update path skips the
                // Store identity-map concurrency dance that Store + Append
                // mix-mode runs into. (See Marten 8 polymorphic-store +
                // events.Append in one session.)
                sa.IsDeleted = true;
                session.Update(sa);
                await session.SaveChangesAsync(ct);

                // Audit #7 — deleting an SA cascade-deletes its credential clients,
                // but a deleted client document does NOT invalidate already-issued
                // M2M tokens. Revoke them by subject (sub = sa.Id) so outstanding
                // reference tokens stop validating immediately.
                var subject = sa.Id.ToString();
                await revoker.RevokeTokensBySubjectAsync(subject, ct);
                await revoker.RevokeAuthorizationsBySubjectAsync(subject, ct);

                dispatcher.DispatchDeletedEvent("ServiceAccount", new ShortGuid(sa.Id).ToString(), session.TenantId);
                return Results.Ok(new { DeletedCredentialCount = deletedCredentialCount });
            })
            .WithName("V2_ServiceAccount_Delete")
            .RequiresPermission("service-account:write");

        // ── SA-scoped credentials (Phase 2C) ──────────────────────────────────
        //
        // A "credential" on a Service Account is a confidential OAuth client
        // pinned to the SA with the single client_credentials grant. The
        // endpoints below are the ONLY mutation path — /admin/oauth/clients
        // rejects mutations on SA-managed clients.

        var credentials = group.MapGroup("{id}/credentials").WithTags("Service Account Credentials");

        credentials.MapGet("", async (ShortGuid id, OAuthAdminService svc, CancellationToken ct) =>
            {
                var list = await svc.ListServiceAccountCredentialsAsync(id.Guid, ct);
                return Results.Ok(list);
            })
            .WithName("V2_ServiceAccount_Credentials_List")
            .RequiresPermission("service-account:read");

        credentials.MapPost("", async (
                ShortGuid id,
                IssueServiceAccountCredentialDto dto,
                OAuthAdminService svc,
                CancellationToken ct) =>
            {
                var result = await svc.IssueServiceAccountCredentialAsync(id.Guid, dto, ct);
                return result.ToResult(issued => Results.Ok(issued));
            })
            .WithName("V2_ServiceAccount_Credentials_Issue")
            .RequiresPermission("service-account:write");

        credentials.MapPut("{credId}", async (
                ShortGuid id,
                string credId,
                UpdateServiceAccountCredentialDto dto,
                OAuthAdminService svc,
                CancellationToken ct) =>
            {
                var result = await svc.UpdateServiceAccountCredentialAsync(id.Guid, credId, dto, ct);
                return result.ToResult(updated => Results.Ok(updated));
            })
            .WithName("V2_ServiceAccount_Credentials_Update")
            .RequiresPermission("service-account:write");

        credentials.MapPost("{credId}/rotate", async (
                ShortGuid id,
                string credId,
                OAuthAdminService svc,
                IOAuthGrantRevoker revoker,
                CancellationToken ct) =>
            {
                var result = await svc.RotateServiceAccountCredentialAsync(id.Guid, credId, ct);
                // Audit #8 — rotating a credential's secret does NOT invalidate tokens
                // already minted with the old secret (a bearer token doesn't re-check
                // the secret). Revoke exactly this client's outstanding tokens
                // (credId == the OAuth application id) so rotation is a real cut-off.
                if (!result.IsError)
                    await revoker.RevokeTokensByApplicationIdAsync(credId, ct);
                return result.ToResult(secret => Results.Ok(secret));
            })
            .WithName("V2_ServiceAccount_Credentials_Rotate")
            .RequiresPermission("service-account:write");

        credentials.MapDelete("{credId}", async (
                ShortGuid id,
                string credId,
                OAuthAdminService svc,
                IOAuthGrantRevoker revoker,
                CancellationToken ct) =>
            {
                var result = await svc.DeleteServiceAccountCredentialAsync(id.Guid, credId, ct);
                if (result.IsError) return result.ToResult();
                // Audit #7 (per-credential) — same as rotate: a deleted client doc
                // doesn't invalidate its already-issued M2M tokens. Revoke them.
                await revoker.RevokeTokensByApplicationIdAsync(credId, ct);
                return Results.NoContent();
            })
            .WithName("V2_ServiceAccount_Credentials_Delete")
            .RequiresPermission("service-account:write");

        return application;
    }

    private static IResult? ValidateAccountName(string normalised)
    {
        if (string.IsNullOrWhiteSpace(normalised))
            return Results.BadRequest(new { Error = "ServiceAccount.AccountNameRequired",
                Message = "Account name is required." });

        if (!AccountNamePattern.IsMatch(normalised))
            return Results.BadRequest(new { Error = "ServiceAccount.InvalidAccountName",
                Message = "Account name must be 2-64 chars, start with a letter or digit, and contain only lowercase letters, digits, dots, hyphens, or underscores." });

        return null;
    }

    private static ServiceAccountDto ToDto(ServiceAccount sa) => new()
    {
        Id = new ShortGuid(sa.Id).ToString(),
        AccountName = sa.AccountName,
        Purpose = sa.Purpose,
        IsActive = sa.IsActive,
        Status = EntityStatus.Active,
    };
}
