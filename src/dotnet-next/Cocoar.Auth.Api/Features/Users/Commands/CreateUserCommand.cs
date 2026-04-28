using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;
using Cocoar.Auth.Application.DTOs.User;
using Cocoar.Auth.Domain.Errors;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authorization.Principals;


namespace Cocoar.Auth.Api.Features.Users.Commands;

public record CreateUserCommand(string? Firstname, string? Lastname, string? Acronym, string? Email, string UserName, string? Password);

public class CreateUserHandler(IDocumentSession session, UserManager<ApplicationUser> userManager)
{
    public async Task<ErrorOr<UserDto>> Handle(
        CreateUserCommand command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.UserName))
            return DomainErrors.User.UserNameRequired;

        var normalizedUserName = command.UserName.Trim().ToLowerInvariant();

        // Check UserName uniqueness (humans only)
        var userNameTaken = await session.Query<Person>()
            .Where(p => p.AccountName == normalizedUserName && !p.IsDeleted)
            .AnyAsync(ct);
        if (userNameTaken)
            return DomainErrors.User.UserNameTaken(normalizedUserName);

        // Check Email uniqueness — persons carry emails directly, groups have
        // their own Email field but we care about collisions across both.
        if (!string.IsNullOrWhiteSpace(command.Email))
        {
            var personEmailTaken = await session.Query<Person>()
                .Where(p => p.Email == command.Email && !p.IsDeleted)
                .AnyAsync(ct);
            if (personEmailTaken)
                return DomainErrors.User.EmailTaken(command.Email);

            var groupEmailTaken = await session.Query<Group>()
                .Where(g => g.Email == command.Email && !g.IsDeleted)
                .AnyAsync(ct);
            if (groupEmailTaken)
                return DomainErrors.User.EmailTaken(command.Email);
        }

        var id = Guid.NewGuid();
        var hasPassword = false;

        var appUser = new ApplicationUser(normalizedUserName, command.Email)
        {
            Id = id,
            Firstname = command.Firstname,
            Lastname = command.Lastname,
            Acronym = command.Acronym,
            IsActive = true
        };

        // Store handles event stream creation (StartStream + UserCreatedEvent + UserUserNameChangedEvent)
        // and document persistence (ApplicationUser + UserSecurityData)
        IdentityResult createResult;
        if (!string.IsNullOrWhiteSpace(command.Password))
        {
            createResult = await userManager.CreateAsync(appUser, command.Password);
            hasPassword = createResult.Succeeded;
        }
        else
        {
            createResult = await userManager.CreateAsync(appUser);
        }

        if (!createResult.Succeeded)
        {
            return Error.Validation("User.IdentityError",
                string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        return new UserDto
        {
            Id = new ShortGuid(id).ToString(),
            Firstname = command.Firstname,
            Lastname = command.Lastname,
            Acronym = command.Acronym,
            Email = command.Email,
            UserName = normalizedUserName,
            IsActive = true,
            HasPassword = hasPassword
        };
    }
}
