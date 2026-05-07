namespace Cocoar.Auth.Application.DTOs.Realms;

public record RealmDto
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[] Domains { get; init; } = [];
    public bool IsControlPlane { get; init; }
    public bool IsActive { get; init; }
    public bool NeedsSetup { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record CreateRealmDto
{
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[]? Domains { get; init; }

    /// <summary>
    /// First-admin invite issued atomically with the realm (C15c).
    /// Required: a realm with no admin path is unusable. The CP-admin
    /// fills UserName + Email; the recipient gets a magic-link mail and
    /// sets their own password — the CP-admin never sees the password,
    /// which keeps SaaS scenarios clean (tenant requester is the only
    /// person who knows the credentials).
    /// </summary>
    public InitialAdminDto InitialAdmin { get; init; } = new();
}

public record InitialAdminDto
{
    public string UserName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
}

public record CreatedRealmDto
{
    public RealmDto Realm { get; init; } = new();

    /// <summary>
    /// Bootstrap-invite metadata returned to the CP-admin who issued the
    /// realm. Includes the magic-link URL — useful in SMTP-less dev
    /// setups where the email isn't actually delivered. The token's
    /// plaintext is part of the URL; the CP-admin should treat this as
    /// secret-equivalent and either copy it to a secure channel or trust
    /// that the recipient will get the email.
    /// </summary>
    public InitialAdminInviteDto InitialAdminInvite { get; init; } = new();
}

public record InitialAdminInviteDto
{
    public string UserName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public string MagicLinkUrl { get; init; } = string.Empty;
}

public record UpdateRealmDto
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string[]? Domains { get; init; }
    public bool? IsActive { get; init; }
}

public record RealmListDto
{
    public List<RealmDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
}
