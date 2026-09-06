using System.Text.Json;
using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Application.Scheduling;
using Modgud.Infrastructure.Scheduling;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Gdpr;
using Modgud.Authentication.Identity;
using Quartz;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// ADR 0018 legacy clean-up — erases the "ghost" accounts the old sign-up paths created
/// BEFORE the proof: passwordless users whose registration code was never redeemed.
/// They are real, event-sourced users (the residue the pending pipeline avoids), so they
/// go through the normal permanent-erase path (masking + archiving), never a raw delete.
///
/// <para><b>Signature</b> (all must hold): <c>EmailConfirmed=false</c>, not deleted, no
/// password, no passkey, no external identity link, no consumed OTP challenge, and the
/// stream is older than <c>olderThanDays</c>. Anything an admin created with a password,
/// or that ever authenticated, is outside the signature by construction.</para>
///
/// <para><b>Dry-run by default.</b> The first runs only log the candidates; an operator
/// flips <c>dryRun</c> to <c>false</c> in the job's parameters once the list looks right.</para>
/// </summary>
[DisallowConcurrentExecution]
public class UnconfirmedRegistrationReaperJob(
    IDocumentSession session,
    IGdprService gdpr,
    ILogger<UnconfirmedRegistrationReaperJob> logger) : IJob
{
    public const string Key = "unconfirmed-registration-reaper";
    public const string Name = "Unconfirmed registration reaper";
    public const string Description =
        "Erases passwordless accounts whose registration code was never redeemed (created by the " +
        "pre-ADR-0018 sign-up paths): unconfirmed, no password, no passkey, no external login, " +
        "no consumed code, older than the configured age. Dry-run by default — set dryRun=false to erase.";

    /// <summary>04:30 UTC daily — after the DCR sweep.</summary>
    public const string DefaultCron = "0 30 4 * * ?";

    public const string DryRunKey = "dryRun";
    public const string OlderThanDaysKey = "olderThanDays";
    private const int DefaultOlderThanDays = 7;

    public static IReadOnlyList<JobParameterField> GetParameterSchema() =>
    [
        new()
        {
            Key = DryRunKey,
            Label = "Dry run",
            Type = JobParameterType.Boolean,
            Default = true,
            Description = "Only log the accounts that would be erased. Set to false to erase them.",
        },
        new()
        {
            Key = OlderThanDaysKey,
            Label = "Older than (days)",
            Type = JobParameterType.Number,
            Default = DefaultOlderThanDays,
            Description = "Only accounts whose stream is older than this many days are candidates.",
        },
    ];

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var cfg = await session.LoadAsync<JobConfig>(Key, ct);
        var raw = cfg?.Parameters ?? new Dictionary<string, object?>();
        var dryRun = ReadBool(raw, DryRunKey) ?? true;
        var olderThanDays = ReadInt(raw, OlderThanDaysKey) ?? DefaultOlderThanDays;
        var (matched, erased) = await RunAsync(dryRun, olderThanDays, ct);

        context.Result = dryRun
            ? (matched == 0 ? "Dry run: no unconfirmed registrations match" : $"Dry run: {matched} account(s) would be erased")
            : (matched == 0 ? "No unconfirmed registrations match" : $"Erased {erased} of {matched} matching account(s)");
    }

    /// <summary>The sweep itself, callable without Quartz. Returns (matched, erased).</summary>
    public async Task<(int Matched, int Erased)> RunAsync(bool dryRun, int olderThanDays, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, olderThanDays));

        var candidates = await session.Query<ApplicationUser>()
            .Where(u => !u.EmailConfirmed && !u.IsDeleted)
            .ToListAsync(ct);

        var matched = 0;
        var erased = 0;
        foreach (var user in candidates)
        {
            if (!await MatchesSignatureAsync(user, cutoff, ct)) continue;
            matched++;

            if (dryRun)
            {
                logger.LogInformation(
                    "Reaper (dry run): would erase unconfirmed passwordless account {UserId} ({Email})",
                    user.Id, LogPiiMasking.MaskEmail(user.Email ?? ""));
                continue;
            }

            var result = await gdpr.PermanentlyEraseAsync(
                user.Id, adminUserId: null,
                reason: "Unconfirmed passwordless registration never completed (ADR 0018 reaper)", ct);
            if (result.IsError)
            {
                logger.LogWarning("Reaper: erase of {UserId} refused: {Error}", user.Id, result.FirstError.Code);
                continue;
            }
            erased++;
        }

        return (matched, erased);
    }

    private async Task<bool> MatchesSignatureAsync(ApplicationUser user, DateTimeOffset cutoff, CancellationToken ct)
    {
        var security = await session.LoadAsync<UserSecurityData>(user.Id, ct);
        if (!string.IsNullOrEmpty(security?.PasswordHash)) return false;

        if (await session.Query<StoredPasskeyCredential>().AnyAsync(c => c.UserId == user.Id, ct)) return false;
        if (await session.Query<ExternalIdentityLink>().AnyAsync(l => l.UserId == user.Id, ct)) return false;

        var challenge = await session.LoadAsync<EmailOtpChallenge>(user.Id, ct);
        if (challenge?.ConsumedAt is not null) return false;

        var stream = await session.Events.FetchStreamStateAsync(user.Id, ct);
        var created = stream?.Created ?? challenge?.CreatedAt;
        return created is not null && created < cutoff;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> raw, string key)
    {
        if (!raw.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            bool b => b,
            JsonElement el => el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
                _ => null,
            },
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> raw, string key)
    {
        if (!raw.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement el => el.ValueKind switch
            {
                JsonValueKind.Number => el.TryGetInt32(out var n) ? n : null,
                JsonValueKind.String when int.TryParse(el.GetString(), out var n) => n,
                _ => null,
            },
            string s when int.TryParse(s, out var n) => n,
            _ => null,
        };
    }
}
