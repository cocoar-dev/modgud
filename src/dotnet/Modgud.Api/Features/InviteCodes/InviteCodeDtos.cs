namespace Modgud.Api.Features.InviteCodes;

/// <summary>Mint request (ADR-0012 §7). <see cref="Count"/> defaults to 1;
/// <see cref="BoundEmail"/> null = bearer codes (D2); <see cref="ExpiresInDays"/>
/// null = the Modgud default of 14 days (D10).</summary>
public sealed record MintInviteCodesDto(int Count = 1, string? BoundEmail = null, int? ExpiresInDays = null);

/// <summary>The plaintext codes, returned exactly once — only hashes are stored.</summary>
public sealed record MintInviteCodesResultDto(IReadOnlyList<string> Codes);

/// <summary>List/read projection of a stored invite code. Never carries the
/// plaintext or the hash — only metadata + status.</summary>
public sealed record InviteCodeDto(
    string Id,
    string AppId,
    string? BoundEmail,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string CreatedBySubject,
    DateTimeOffset? UsedAt,
    string? UsedByUserId,
    string Status);
