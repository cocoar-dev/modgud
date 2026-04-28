using TimeToDo.Domain.Common;

namespace TimeToDo.Authentication.Api.Account;

/// <summary>
/// Payload for <see cref="TimeToDo.Domain.Identity.ChangeRequestType.Profile"/> change
/// requests. A property with <see cref="Optional{T}.HasValue"/> = false means "not part
/// of this request"; with HasValue = true it is the desired new value. Adding a new
/// self-service-editable profile field only needs a property here plus a branch in the
/// admin-approve handler.
/// </summary>
public class ProfileUpdateDto
{
    public Optional<string> Firstname { get; set; }
    public Optional<string> Lastname { get; set; }
    public Optional<string?> Acronym { get; set; }
    public Optional<string?> Email { get; set; }
}
