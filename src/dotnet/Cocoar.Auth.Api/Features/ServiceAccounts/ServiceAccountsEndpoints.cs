using System.Text.RegularExpressions;
using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Cocoar.Auth.Application.DTOs.ServiceAccount;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Domain.ValueObjects;
using Marten;

namespace Cocoar.Auth.Api.Features.ServiceAccounts;

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

        group.MapPost("", async (ServiceAccountCreateDto dto, IDocumentSession session, DataEventDispatcher dispatcher) =>
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

                var sa = new ServiceAccount
                {
                    Id = Guid.NewGuid(),
                    AccountName = normalised,
                    Purpose = string.IsNullOrWhiteSpace(dto.Purpose) ? null : dto.Purpose.Trim(),
                    IsActive = true,
                };
                session.Store(sa);
                await session.SaveChangesAsync();

                var created = ToDto(sa);
                dispatcher.DispatchCreatedEvent("ServiceAccount", created);
                return Results.Ok(created);
            })
            .WithName("V2_ServiceAccount_Create")
            .RequiresPermission("service-account:write");

        group.MapPut("{id}", async (ShortGuid id, ServiceAccountUpdateDto dto, IDocumentSession session, DataEventDispatcher dispatcher) =>
            {
                var sa = await session.LoadAsync<ServiceAccount>(id.Guid);
                if (sa is null || sa.IsDeleted) return Results.NotFound();

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

                        sa.AccountName = normalised;
                    }
                }

                if (dto.Purpose is not null)
                    sa.Purpose = string.IsNullOrWhiteSpace(dto.Purpose) ? null : dto.Purpose.Trim();

                if (dto.IsActive.HasValue)
                    sa.IsActive = dto.IsActive.Value;

                session.Store(sa);
                await session.SaveChangesAsync();
                var updated = ToDto(sa);
                dispatcher.DispatchUpdatedEvent("ServiceAccount", updated);
                return Results.Ok(updated);
            })
            .WithName("V2_ServiceAccount_Update")
            .RequiresPermission("service-account:write");

        group.MapDelete("{id}", async (ShortGuid id, IDocumentSession session, DataEventDispatcher dispatcher) =>
            {
                var sa = await session.LoadAsync<ServiceAccount>(id.Guid);
                if (sa is null || sa.IsDeleted) return Results.NotFound();

                // Soft-delete — keeps audit / role-membership references
                // resolvable. Matches how Person soft-deletes work.
                sa.IsDeleted = true;
                session.Store(sa);
                await session.SaveChangesAsync();
                dispatcher.DispatchDeletedEvent("ServiceAccount", new ShortGuid(sa.Id).ToString());
                return Results.NoContent();
            })
            .WithName("V2_ServiceAccount_Delete")
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
