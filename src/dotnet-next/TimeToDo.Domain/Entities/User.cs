using ErrorOr;
using TimeToDo.Domain.Errors;

namespace TimeToDo.Domain.Entities;

/// <summary>
/// Simple domain entity for User.
/// </summary>
public class User
{
    public Guid Id { get; private set; }
    public string? Firstname { get; private set; }
    public string? Lastname { get; private set; }
    public string? Acronym { get; private set; }
    public string? Email { get; private set; }

    private User() { }

    /// <summary>
    /// Factory method to create a new User with validation.
    /// </summary>
    public static ErrorOr<User> Create(
        string? firstname,
        string? lastname,
        string? acronym = null,
        string? email = null,
        Guid? id = null)
    {
        // At least firstname or lastname should be provided
        if (string.IsNullOrWhiteSpace(firstname) && string.IsNullOrWhiteSpace(lastname))
            return DomainErrors.User.FirstnameRequired;

        return new User
        {
            Id = id ?? Guid.NewGuid(),
            Firstname = firstname,
            Lastname = lastname,
            Acronym = acronym,
            Email = email
        };
    }

    /// <summary>
    /// Reconstitutes a User from persistence data.
    /// </summary>
    public static User Reconstitute(
        Guid id,
        string? firstname,
        string? lastname,
        string? acronym,
        string? email)
    {
        return new User
        {
            Id = id,
            Firstname = firstname,
            Lastname = lastname,
            Acronym = acronym,
            Email = email
        };
    }

    /// <summary>
    /// Updates the User's properties.
    /// </summary>
    public ErrorOr<Success> Update(
        string? firstname,
        string? lastname,
        string? acronym,
        string? email)
    {
        if (string.IsNullOrWhiteSpace(firstname) && string.IsNullOrWhiteSpace(lastname))
            return DomainErrors.User.FirstnameRequired;

        Firstname = firstname;
        Lastname = lastname;
        Acronym = acronym;
        Email = email;

        return Result.Success;
    }

    /// <summary>
    /// Gets the display name for this user.
    /// </summary>
    public string GetDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(Firstname) && !string.IsNullOrWhiteSpace(Lastname))
            return $"{Firstname} {Lastname}";
        if (!string.IsNullOrWhiteSpace(Firstname))
            return Firstname;
        if (!string.IsNullOrWhiteSpace(Lastname))
            return Lastname;
        if (!string.IsNullOrWhiteSpace(Acronym))
            return Acronym;
        return Id.ToString();
    }
}
