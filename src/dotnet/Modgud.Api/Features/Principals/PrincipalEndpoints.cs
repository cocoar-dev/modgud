using BuildingBlocks.Helper;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Marten;
using Modgud.Authentication.Domain;

namespace Modgud.Api.Features.Principals;

public static class PrincipalEndpoints
{
    public static WebApplication MapPrincipalEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/principal")
            .WithTags("Principals")
            .RequireAuthorization();

        // Cross-type lookup for pickers (member select, responsible select, …).
        // Returns all active, non-deleted principals — Persons + Groups +
        // ServiceAccounts, including the type-specific fields a rich item-row
        // needs (acronym + full name for persons, description for groups,
        // purpose for service-accounts).
        group.MapGet("lookup", async (IDocumentSession session) =>
        {
            var principals = await session.Query<Principal>()
                .Where(p => !p.IsDeleted && p.IsActive)
                .ToListAsync();

            return Results.Ok(principals
                .OrderBy(p => p switch
                {
                    Person => 0,
                    Group => 1,
                    ServiceAccount => 2,
                    _ => 3,
                })
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(p =>
                {
                    var person = p as Person;
                    var groupP = p as Group;
                    var serviceAccount = p as ServiceAccount;
                    return new
                    {
                        Id = new ShortGuid(p.Id).ToString(),
                        Label = p.DisplayName,
                        Type = p.Type,
                        // UserName is the login-handle equivalent. For
                        // Persons it's the AccountName, for ServiceAccounts
                        // the AccountName too (same uniqueness namespace).
                        UserName = person?.AccountName ?? serviceAccount?.AccountName,
                        Firstname = person?.Firstname,
                        Lastname = person?.Lastname,
                        Acronym = person?.Acronym,
                        // Description doubles as the "Purpose" line for
                        // ServiceAccounts in the picker subtitle.
                        Description = groupP?.Description ?? serviceAccount?.Purpose,
                        // Audit #19 — Email deliberately dropped. It was the
                        // disproportionate PII in this directory dump and the picker
                        // (the only consumer) never rendered it. Names/acronym stay
                        // because the member picker shows them; the leak was email
                        // enumeration, now gone, and the endpoint requires
                        // authorization-group:read so a zero-role user can't dump the
                        // directory at all.
                    };
                }));
        })
        .WithName("Principal_Lookup")
        // Audit #19 — was authenticated-only (.RequireAuthorization at the group),
        // which let any zero-role user enumerate the whole realm directory. The sole
        // consumer is the group-member picker, already gated on this permission.
        .RequiresPermission("authorization-group:read");

        return application;
    }
}
