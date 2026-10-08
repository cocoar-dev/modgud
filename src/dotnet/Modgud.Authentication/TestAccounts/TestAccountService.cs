using System.Text.RegularExpressions;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Principals;
using Group = Modgud.Authorization.Principals.Group;
using Modgud.Infrastructure.Audit;

namespace Modgud.Authentication.TestAccounts;

/// <summary>What the admin console shows about a test account.</summary>
public record TestAccountStatus(
    bool IsTestAccount,
    bool HasFixedEmailCode,
    DateTimeOffset? FixedEmailCodeExpiresAt,
    DateTimeOffset? FixedEmailCodeSetAt,
    DateTimeOffset? FixedEmailCodeLastUsedAt,
    string? FixedEmailCodeLastUsedClientId);

/// <summary>The outcome of checking a code against a test account's fixed code.</summary>
public enum FixedEmailCodeResult
{
    /// <summary>The account has no usable fixed code: use the regular e-mail code.</summary>
    NotApplicable,
    Accepted,
    Rejected,
}

/// <summary>
/// ADR 0026 — test accounts: the marker, its administration barrier and the fixed
/// e-mail code. Marking and the capabilities are set by a person in the admin console
/// (the endpoints gate on <c>user:test-account</c> under <c>/api/admin</c>); this service
/// holds the rules.
/// </summary>
public interface ITestAccountService
{
    Task<TestAccountStatus?> GetStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Marks or unmarks the account. Unmarking deletes every capability.</summary>
    Task<ErrorOr<Success>> SetMarkerAsync(Guid userId, bool isTestAccount, Guid? actorId, CancellationToken ct = default);

    Task<ErrorOr<Success>> SetFixedEmailCodeAsync(
        Guid userId, string code, DateTimeOffset? expiresAt, Guid? actorId, CancellationToken ct = default);

    Task<ErrorOr<Success>> RemoveFixedEmailCodeAsync(Guid userId, Guid? actorId, CancellationToken ct = default);

    /// <summary>Whether a sign-in by e-mail code for this account uses its fixed code
    /// (so no code is sent and no cooldown applies).</summary>
    Task<bool> HasUsableFixedEmailCodeAsync(ApplicationUser user, CancellationToken ct = default);

    /// <summary>Checks <paramref name="code"/> against the account's fixed code, applying
    /// the progressive delay and logging every use.</summary>
    Task<FixedEmailCodeResult> VerifyFixedEmailCodeAsync(
        ApplicationUser user, string code, string? ipAddress, string? clientId, CancellationToken ct = default);
}

public sealed partial class TestAccountService(
    IDocumentSession session,
    IPasswordHasher<ApplicationUser> hasher,
    IAutoMembershipRecalculator recalculator,
    ISecurityAuditLog audit,
    TimeProvider time,
    ILogger<TestAccountService> logger) : ITestAccountService
{
    /// <summary>Wrong codes before the delay starts.</summary>
    internal const int FreeAttempts = 3;

    /// <summary>The longest wait between two checked attempts.</summary>
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex SixDigits();

    public async Task<TestAccountStatus?> GetStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (user is null || user.IsDeleted) return null;
        var code = user.IsTestAccount ? await session.LoadAsync<TestAccountEmailCode>(userId, ct) : null;
        return new TestAccountStatus(
            user.IsTestAccount,
            code is not null,
            code?.ExpiresAt,
            code?.SetAt,
            code?.LastUsedAt,
            code?.LastUsedClientId);
    }

    public async Task<ErrorOr<Success>> SetMarkerAsync(Guid userId, bool isTestAccount, Guid? actorId, CancellationToken ct = default)
    {
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        // Not a test account and nothing to sign in with: unmarking has nothing to do.
        if (user is null && !isTestAccount) return Result.Success;
        if (user is null || user.IsDeleted)
            return Error.NotFound("TestAccount.UserNotFound", "User not found.");
        if (user.IsTestAccount == isTestAccount) return Result.Success;

        if (isTestAccount)
        {
            var conflict = await ClosedGroupsOfAsync(userId, ct);
            if (conflict.Count > 0)
                return Error.Validation("TestAccount.InClosedGroup",
                    "A test account can be in no group that grants Modgud's own administration or excludes test accounts. "
                    + $"Remove the account from: {string.Join(", ", conflict)}.");
        }

        user.IsTestAccount = isTestAccount;
        session.Store(user);
        session.Events.Append(userId, new UserTestAccountChangedEvent(userId, isTestAccount, actorId));

        // Unmarking ends every capability: a forgotten secret must not come back when
        // the account is marked again.
        if (!isTestAccount)
            session.Delete<TestAccountEmailCode>(userId);

        await session.SaveChangesAsync(ct);

        // Auto groups closed to test accounts drop the account now (and an unmarked one
        // may rejoin them). The marker is on the Person the recalculator loads.
        await recalculator.RecalculateForPrincipalAsync(userId, session, ct: ct);
        await session.SaveChangesAsync(ct);

        logger.LogInformation("Test-account marker {State} for user {UserId} by {ActorId}",
            isTestAccount ? "set" : "removed", userId, actorId);
        return Result.Success;
    }

    public async Task<ErrorOr<Success>> SetFixedEmailCodeAsync(
        Guid userId, string code, DateTimeOffset? expiresAt, Guid? actorId, CancellationToken ct = default)
    {
        var user = await session.LoadAsync<ApplicationUser>(userId, ct);
        if (user is null || user.IsDeleted)
            return Error.NotFound("TestAccount.UserNotFound", "User not found.");
        if (!user.IsTestAccount)
            return Error.Validation("TestAccount.NotMarked", "Only a test account can have a fixed e-mail code.");
        if (string.IsNullOrEmpty(code) || !SixDigits().IsMatch(code))
            return Error.Validation("TestAccount.CodeFormat", "The fixed e-mail code is exactly six digits, like a sent code.");
        var now = time.GetUtcNow();
        if (expiresAt is { } expires && expires <= now)
            return Error.Validation("TestAccount.ExpiryInPast", "The expiry date must be in the future.");

        var row = await session.LoadAsync<TestAccountEmailCode>(userId, ct)
                  ?? new TestAccountEmailCode { Id = userId };
        row.CodeHash = hasher.HashPassword(user, code);
        row.ExpiresAt = expiresAt;
        row.SetAt = now;
        row.SetByUserId = actorId;
        row.FailedAttempts = 0;
        row.RetryNotBefore = null;
        session.Store(row);
        audit.StoreRequired(session, new SecurityAuditRecord
        {
            EventType = AuditEvents.TestAccountCodeSet,
            ActorKind = AuditActorKind.User,
            ActorSubjectId = actorId,
            TargetSubjectId = userId,
            OutcomeCode = AuditOutcomes.Succeeded,
            EffectiveAt = expiresAt,
        });
        await session.SaveChangesAsync(ct);
        return Result.Success;
    }

    public async Task<ErrorOr<Success>> RemoveFixedEmailCodeAsync(Guid userId, Guid? actorId, CancellationToken ct = default)
    {
        var row = await session.LoadAsync<TestAccountEmailCode>(userId, ct);
        if (row is null) return Result.Success;
        session.Delete(row);
        audit.StoreRequired(session, new SecurityAuditRecord
        {
            EventType = AuditEvents.TestAccountCodeRemoved,
            ActorKind = AuditActorKind.User,
            ActorSubjectId = actorId,
            TargetSubjectId = userId,
            OutcomeCode = AuditOutcomes.Succeeded,
        });
        await session.SaveChangesAsync(ct);
        return Result.Success;
    }

    public async Task<bool> HasUsableFixedEmailCodeAsync(ApplicationUser user, CancellationToken ct = default)
    {
        if (!user.IsTestAccount) return false;
        var row = await session.LoadAsync<TestAccountEmailCode>(user.Id, ct);
        return row is not null && !row.IsExpired(time.GetUtcNow());
    }

    public async Task<FixedEmailCodeResult> VerifyFixedEmailCodeAsync(
        ApplicationUser user, string code, string? ipAddress, string? clientId, CancellationToken ct = default)
    {
        if (!user.IsTestAccount) return FixedEmailCodeResult.NotApplicable;
        var row = await session.LoadAsync<TestAccountEmailCode>(user.Id, ct);
        var now = time.GetUtcNow();
        // No code, or an expired one: the account signs in like any other, with a sent
        // code (the request path mails one again once the fixed code has expired).
        if (row is null || row.IsExpired(now)) return FixedEmailCodeResult.NotApplicable;

        // Progressive delay: inside the wait the code is not even checked, so the
        // answer carries no information about it.
        if (row.RetryNotBefore is { } notBefore && notBefore > now)
            return FixedEmailCodeResult.Rejected;

        var trimmed = code.Trim();
        var ok = SixDigits().IsMatch(trimmed)
                 && hasher.VerifyHashedPassword(user, row.CodeHash, trimmed) != PasswordVerificationResult.Failed;

        if (ok)
        {
            row.FailedAttempts = 0;
            row.RetryNotBefore = null;
            row.LastUsedAt = now;
            row.LastUsedClientId = clientId;
        }
        else
        {
            row.FailedAttempts++;
            row.RetryNotBefore = row.FailedAttempts > FreeAttempts ? now + DelayAfter(row.FailedAttempts) : null;
        }
        session.Store(row);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            // A concurrent attempt or admin change won; this attempt counts as failed.
            return FixedEmailCodeResult.Rejected;
        }

        var record = new SecurityAuditRecord
        {
            EventType = ok ? AuditEvents.TestAccountCodeUsed : AuditEvents.TestAccountCodeRejected,
            ActorKind = AuditActorKind.User,
            ActorSubjectId = user.Id,
            TargetSubjectId = user.Id,
            IpAddress = ipAddress,
            OAuthClientId = clientId,
            AuthenticationMethod = "test_account_code",
            OutcomeCode = ok ? AuditOutcomes.Succeeded : AuditOutcomes.Rejected,
            Count = ok ? null : row.FailedAttempts,
        };
        if (ok) await audit.RecordRequiredAsync(record, ct);
        else audit.RecordAbuse(record);

        return ok ? FixedEmailCodeResult.Accepted : FixedEmailCodeResult.Rejected;
    }

    /// <summary>1 s after the first delayed failure, doubling, at most <see cref="MaxDelay"/>.</summary>
    internal static TimeSpan DelayAfter(int failedAttempts)
    {
        var exponent = Math.Min(failedAttempts - FreeAttempts - 1, 20);
        var seconds = Math.Pow(2, Math.Max(exponent, 0));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    /// <summary>
    /// Names of the groups closed to test accounts that the user would stay in: reached
    /// from a manual group membership (an auto group is recomputed without the account).
    /// </summary>
    private async Task<List<string>> ClosedGroupsOfAsync(Guid userId, CancellationToken ct)
    {
        var groups = await session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync(ct);
        var administrationRoleIds = await TestAccountGroupPolicy.AdministrationRoleIdsAsync(session, ct);
        var parents = groups
            .SelectMany(g => g.MemberIds.Select(m => (Member: m, Group: g)))
            .ToLookup(x => x.Member, x => x.Group);

        var closed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Guid>();
        var queue = new Queue<Group>(parents[userId].Where(g => g.MembershipMode == MembershipMode.Manual));
        while (queue.Count > 0)
        {
            var group = queue.Dequeue();
            if (!visited.Add(group.Id)) continue;
            if (TestAccountGroupPolicy.IsClosed(group, administrationRoleIds)) closed.Add(group.Name);
            foreach (var parent in parents[group.Id]) queue.Enqueue(parent);
        }
        return [.. closed];
    }
}
