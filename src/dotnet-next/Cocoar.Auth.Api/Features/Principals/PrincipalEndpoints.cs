using BuildingBlocks.Helper;
using Cocoar.Auth.Authorization.Principals;
using Marten;
using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Api.Features.Principals;

public static class PrincipalEndpoints
{
    public static WebApplication MapPrincipalEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/principal")
            .WithTags("Principals")
            .RequireAuthorization();

        // Cross-type lookup for pickers (member select, responsible select, …).
        // Returns all active, non-deleted principals — Persons AND Groups, including
        // the type-specific fields a rich item-row needs (acronym + full name for
        // persons, description for groups).
        group.MapGet("lookup", async (IDocumentSession session) =>
        {
            var principals = await session.Query<Principal>()
                .Where(p => !p.IsDeleted && p.IsActive)
                .ToListAsync();

            return Results.Ok(principals
                .OrderBy(p => p is Group ? 1 : 0)    // Persons first
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(p =>
                {
                    var person = p as Person;
                    var groupP = p as Group;
                    return new
                    {
                        Id = new ShortGuid(p.Id).ToString(),
                        Label = p.DisplayName,
                        Type = p.Type,
                        UserName = (p as Cocoar.Auth.Authorization.Principals.Person)?.AccountName,
                        Firstname = person?.Firstname,
                        Lastname = person?.Lastname,
                        Acronym = person?.Acronym,
                        Description = groupP?.Description,
                        Email = person?.Email ?? groupP?.Email,
                    };
                }));
        })
        .WithName("Principal_Lookup");

        return application;
    }
}
