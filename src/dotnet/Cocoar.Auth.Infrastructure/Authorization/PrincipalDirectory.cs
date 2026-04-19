using Cocoar.Auth.Domain.Principals;
using Marten.Schema;

namespace Cocoar.Auth.Infrastructure.Authorization;

/// <summary>
/// Inline, synchronously-consistent directory of all principals (Persons, Groups,
/// future ServiceAccounts, ...). Backs cross-type lookup, auth, uniqueness checks,
/// and auto-membership predicate evaluation — everything that needs to see the
/// current state immediately after an event commits.
/// <para>
/// Type-specific fields live in nested records (<see cref="PersonData"/>,
/// <see cref="GroupData"/>) so adding a Person-only field does not pollute Groups
/// with a null column. Queries use JSONB paths via Marten
/// (<c>data-&gt;'Person'-&gt;&gt;'Firstname'</c>), which Marten's LINQ provider handles
/// natively.
/// </para>
/// <para>
/// Kept lean by design: only cheap-to-compute fields belong here. Cross-stream
/// aggregations (group counts, computed permissions, etc.) belong in async read
/// models — embedding them here would turn every source event into an N*M
/// re-computation inside the commit transaction.
/// </para>
/// </summary>
[DocumentAlias("principal_directory")]
public record PrincipalDirectory : IPrincipal
{
    public Guid Id { get; init; }
    public string Type { get; init; } = PrincipalType.Person;
    public bool IsActive { get; init; } = true;
    public bool IsDeleted { get; init; }
    public bool CanAuthenticate { get; init; }
    public bool IsContainer { get; init; }

    /// <summary>Universal email (Person's address, or Group's shared address).</summary>
    public string? Email { get; init; }
    public string? NormalizedEmail { get; init; }

    /// <summary>Person-specific data — non-null iff <c>Type == PrincipalType.Person</c>.</summary>
    public PersonData? Person { get; init; }

    /// <summary>Group-specific data — non-null iff <c>Type == PrincipalType.Group</c>.</summary>
    public GroupData? Group { get; init; }

    /// <summary>
    /// Computes a display-friendly label. For Persons:
    /// "Firstname Lastname", fallback to UserName. For Groups: the group name.
    /// </summary>
    public string GetDisplayLabel()
    {
        if (Type == PrincipalType.Group)
            return Group?.Name ?? "";

        var person = Person;
        if (person is null) return "";

        var fullName = $"{person.Firstname ?? ""} {person.Lastname ?? ""}".Trim();
        return !string.IsNullOrWhiteSpace(fullName) ? fullName : (person.UserName ?? "");
    }

    string IPrincipal.DisplayName => GetDisplayLabel();
}

/// <summary>Person-specific directory fields. Null when the principal is not a Person.</summary>
public record PersonData
{
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
    public string? UserName { get; init; }
    public string? NormalizedUserName { get; init; }
    public string? PhoneNumber { get; init; }
}

/// <summary>Group-specific directory fields. Null when the principal is not a Group.</summary>
public record GroupData
{
    public string Name { get; init; } = string.Empty;
    public EmailMode EmailMode { get; init; } = EmailMode.Shared;
}
