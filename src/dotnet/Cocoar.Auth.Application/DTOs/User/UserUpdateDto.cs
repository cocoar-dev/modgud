using Cocoar.Auth.Domain.Common;

namespace Cocoar.Auth.Application.DTOs.User;

public class UserUpdateDto
{
    public Optional<string> Firstname { get; set; }
    public Optional<string> Lastname { get; set; }
    public Optional<string> Acronym { get; set; }
    public Optional<string> Email { get; set; }
    public Optional<string> UserName { get; set; }

    public Optional<bool> IsActive { get; set; }
    /// <summary>
    /// Admin override for the Identity EmailConfirmed flag. Lets the admin
    /// vouch for an email at create/edit time without forcing the user
    /// through the magic-link verify round-trip.
    /// </summary>
    public Optional<bool> EmailConfirmed { get; set; }
}
