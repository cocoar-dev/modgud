using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;

namespace Modgud.Api.Features.Auth.Staffing;

/// <summary>The staffing-specific seam around concrete credential systems.
/// Method IDs and capability metadata are immutable once shipped.</summary>
public interface IActivationProof
{
    string MethodId { get; }
    ProofCapability Capabilities { get; }
    ActivationProofOwnerKind OwnerKind { get; }

    Task<ActivationChallenge> BeginAsync(ActivationContext context, CancellationToken ct);
    Task<ActivationResult> CompleteAsync(ActivationContext context, string response, CancellationToken ct);
    Task<ActivationChallenge> BeginCandidatesAsync(
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        ActivationBeginInput input,
        CancellationToken ct);
    Task<CandidateActivationResult> CompleteCandidatesAsync(
        StaffingCeremony ceremony,
        string response,
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        CancellationToken ct);
    Task<bool> RevalidateAsync(ActivationEvidence evidence, PositionPrincipal position, CancellationToken ct);
    void RegisterInvalidationHooks(IActivationInvalidationRegistry registry);
}

public sealed record ActivationContext(
    PositionPrincipal Position,
    TerminalEnrollment Terminal,
    StaffingCeremony? Ceremony = null,
    ActivationBeginInput? BeginInput = null);

public sealed record ActivationBeginInput(string? MethodId, string? AccountName, string? PositionId = null);

public sealed record ActivationChallenge(
    StaffingCeremony? Ceremony,
    string? OptionsJson,
    ActivationProofFailure? Failure,
    string ResponseProperty = "publicKey")
{
    public static ActivationChallenge Failed(string code, string message) =>
        new(null, null, new ActivationProofFailure(code, message));
}

public sealed class PersonalPasswordActivationProof(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) : IActivationProof
{
    public string MethodId => ActivationProofMethodIds.PersonalPassword;
    public ProofCapability Capabilities => ProofCapability.IdentifiedActor;
    public ActivationProofOwnerKind OwnerKind => ActivationProofOwnerKind.Personal;

    public async Task<ActivationChallenge> BeginAsync(ActivationContext context, CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ResolveSubjectAsync(
            context, session, userManager, signInManager, ct);
        if (subject.Failure is not null) return subject.Failure;

        var ceremony = await PersonalTextProofSupport.CreateCeremonyAsync(
            context, MethodId, subject.User!, subject.Grant!, session, ct);
        return new ActivationChallenge(
            ceremony,
            JsonSerializer.Serialize(new { Fields = new[] { "password" } }),
            null,
            "challenge");
    }

    public async Task<ActivationResult> CompleteAsync(
        ActivationContext context, string response, CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ReloadSubjectAsync(
            context, session, userManager, signInManager, ct);
        if (subject.Failure is not null)
            return ActivationResult.Failed(subject.Failure.Failure!.Code, subject.Failure.Failure.Message);

        var password = PersonalTextProofSupport.ReadSecret(response, "password");
        if (string.IsNullOrEmpty(password) ||
            !await userManager.HasPasswordAsync(subject.User!) ||
            !await userManager.CheckPasswordAsync(subject.User!, password))
        {
            await PersonalTextProofSupport.RecordFailureAsync(session, subject.Grant!, ct);
            return ActivationResult.Failed("Staffing.PasswordFailed", "Password verification failed.");
        }

        await PersonalTextProofSupport.RecordSuccessAsync(session, subject.Grant!, ct);
        var stamp = await userManager.GetSecurityStampAsync(subject.User!);
        return new ActivationResult(new ActivationEvidence
        {
            MethodId = MethodId,
            UserId = subject.User!.Id,
            GrantId = subject.Grant!.Id,
            CredentialId = PersonalTextProofSupport.PasswordCredentialVersion(stamp),
            Binding = context.Terminal.Binding,
        }, null);
    }

    public async Task<ActivationChallenge> BeginCandidatesAsync(
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        ActivationBeginInput input,
        CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ResolveCandidateSubjectAsync(
            positions, terminal, input, session, userManager, signInManager, ct);
        if (subject.Failure is not null) return subject.Failure;
        var ceremony = await PersonalTextProofSupport.CreateCandidateCeremonyAsync(
            positions, terminal, MethodId, subject.User!, session, ct);
        return new ActivationChallenge(
            ceremony,
            JsonSerializer.Serialize(new { Fields = new[] { "password" } }),
            null,
            "challenge");
    }

    public async Task<CandidateActivationResult> CompleteCandidatesAsync(
        StaffingCeremony ceremony,
        string response,
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ReloadCandidateSubjectAsync(
            ceremony, positions, session, userManager, signInManager, ct);
        if (subject.Failure is not null)
            return CandidateActivationResult.Failed(
                subject.Failure.Failure!.Code, subject.Failure.Failure.Message);

        var password = PersonalTextProofSupport.ReadSecret(response, "password");
        if (string.IsNullOrEmpty(password) ||
            !await userManager.HasPasswordAsync(subject.User!) ||
            !await userManager.CheckPasswordAsync(subject.User!, password))
        {
            await PersonalTextProofSupport.RecordFailuresAsync(session, subject.Grants, ct);
            return CandidateActivationResult.Failed(
                "Staffing.PasswordFailed", "Password verification failed.");
        }

        await PersonalTextProofSupport.RecordSuccessesAsync(session, subject.Grants, ct);
        var credentialVersion = PersonalTextProofSupport.PasswordCredentialVersion(
            await userManager.GetSecurityStampAsync(subject.User!));
        return new CandidateActivationResult(subject.Grants.Select(grant =>
            new StaffingCandidateEvidence(grant.PositionPrincipalId, new ActivationEvidence
            {
                MethodId = MethodId,
                UserId = subject.User!.Id,
                GrantId = grant.Id,
                CredentialId = credentialVersion,
                Binding = terminal.Binding,
            })).ToArray(), null);
    }

    public async Task<bool> RevalidateAsync(ActivationEvidence evidence, PositionPrincipal position, CancellationToken ct)
    {
        if (evidence.UserId is not { } userId || evidence.GrantId is not { } grantId ||
            evidence.CredentialId is not { } credentialVersion)
            return false;
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || !await userManager.HasPasswordAsync(user))
            return false;
        var stamp = await userManager.GetSecurityStampAsync(user);
        if (PersonalTextProofSupport.PasswordCredentialVersion(stamp) != credentialVersion)
            return false;
        return await PersonalTextProofSupport.GrantIsActiveAsync(session, grantId, userId, ct);
    }

    public void RegisterInvalidationHooks(IActivationInvalidationRegistry registry)
    {
        registry.Register("user-disabled", MethodId);
        registry.Register("password-changed", MethodId);
        registry.Register("position-grant-suspended", MethodId);
        registry.Register("position-grant-revoked", MethodId);
    }
}

public sealed class PersonalEmailOtpActivationProof(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IEmailOtpService emailOtpService) : IActivationProof
{
    public string MethodId => ActivationProofMethodIds.PersonalEmailOtp;
    public ProofCapability Capabilities => ProofCapability.IdentifiedActor;
    public ActivationProofOwnerKind OwnerKind => ActivationProofOwnerKind.Personal;

    public async Task<ActivationChallenge> BeginAsync(ActivationContext context, CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ResolveSubjectAsync(
            context, session, userManager, signInManager, ct);
        if (subject.Failure is not null) return subject.Failure;
        if (!subject.User!.EmailOtpEnabled || !subject.User.EmailConfirmed || string.IsNullOrWhiteSpace(subject.User.Email))
            return ActivationChallenge.Failed(
                "Staffing.EmailOtpUnavailable", "Email OTP is not available for this account.");

        var issue = await emailOtpService.RequestOtpAsync(subject.User.Id, ct);
        if (issue.IsError)
            return ActivationChallenge.Failed(
                "Staffing.EmailOtpUnavailable", "Email OTP could not be issued.");

        var ceremony = await PersonalTextProofSupport.CreateCeremonyAsync(
            context, MethodId, subject.User, subject.Grant!, session, ct);
        return new ActivationChallenge(
            ceremony,
            JsonSerializer.Serialize(new { Delivery = "email", Fields = new[] { "code" } }),
            null,
            "challenge");
    }

    public async Task<ActivationResult> CompleteAsync(
        ActivationContext context, string response, CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ReloadSubjectAsync(
            context, session, userManager, signInManager, ct);
        if (subject.Failure is not null)
            return ActivationResult.Failed(subject.Failure.Failure!.Code, subject.Failure.Failure.Message);
        if (!subject.User!.EmailOtpEnabled)
            return ActivationResult.Failed("Staffing.EmailOtpFailed", "Email OTP verification failed.");

        var code = PersonalTextProofSupport.ReadSecret(response, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            await PersonalTextProofSupport.RecordFailureAsync(session, subject.Grant!, ct);
            return ActivationResult.Failed("Staffing.EmailOtpFailed", "Email OTP verification failed.");
        }
        var verified = await emailOtpService.VerifyOtpAsync(subject.User.Id, code, ct);
        if (verified.IsError)
        {
            await PersonalTextProofSupport.RecordFailureAsync(session, subject.Grant!, ct);
            return ActivationResult.Failed("Staffing.EmailOtpFailed", "Email OTP verification failed.");
        }

        await PersonalTextProofSupport.RecordSuccessAsync(session, subject.Grant!, ct);
        return new ActivationResult(new ActivationEvidence
        {
            MethodId = MethodId,
            UserId = subject.User.Id,
            GrantId = subject.Grant!.Id,
            Binding = context.Terminal.Binding,
        }, null);
    }

    public async Task<ActivationChallenge> BeginCandidatesAsync(
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        ActivationBeginInput input,
        CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ResolveCandidateSubjectAsync(
            positions, terminal, input, session, userManager, signInManager, ct);
        if (subject.Failure is not null) return subject.Failure;
        if (!subject.User!.EmailOtpEnabled || !subject.User.EmailConfirmed ||
            string.IsNullOrWhiteSpace(subject.User.Email))
            return ActivationChallenge.Failed(
                "Staffing.EmailOtpUnavailable", "Email OTP is not available for this account.");

        var issue = await emailOtpService.RequestOtpAsync(subject.User.Id, ct);
        if (issue.IsError)
            return ActivationChallenge.Failed(
                "Staffing.EmailOtpUnavailable", "Email OTP could not be issued.");
        var ceremony = await PersonalTextProofSupport.CreateCandidateCeremonyAsync(
            positions, terminal, MethodId, subject.User, session, ct);
        return new ActivationChallenge(
            ceremony,
            JsonSerializer.Serialize(new { Delivery = "email", Fields = new[] { "code" } }),
            null,
            "challenge");
    }

    public async Task<CandidateActivationResult> CompleteCandidatesAsync(
        StaffingCeremony ceremony,
        string response,
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        CancellationToken ct)
    {
        var subject = await PersonalTextProofSupport.ReloadCandidateSubjectAsync(
            ceremony, positions, session, userManager, signInManager, ct);
        if (subject.Failure is not null)
            return CandidateActivationResult.Failed(
                subject.Failure.Failure!.Code, subject.Failure.Failure.Message);
        if (!subject.User!.EmailOtpEnabled)
            return CandidateActivationResult.Failed(
                "Staffing.EmailOtpFailed", "Email OTP verification failed.");

        var code = PersonalTextProofSupport.ReadSecret(response, "code");
        if (string.IsNullOrWhiteSpace(code) ||
            (await emailOtpService.VerifyOtpAsync(subject.User.Id, code, ct)).IsError)
        {
            await PersonalTextProofSupport.RecordFailuresAsync(session, subject.Grants, ct);
            return CandidateActivationResult.Failed(
                "Staffing.EmailOtpFailed", "Email OTP verification failed.");
        }

        await PersonalTextProofSupport.RecordSuccessesAsync(session, subject.Grants, ct);
        return new CandidateActivationResult(subject.Grants.Select(grant =>
            new StaffingCandidateEvidence(grant.PositionPrincipalId, new ActivationEvidence
            {
                MethodId = MethodId,
                UserId = subject.User.Id,
                GrantId = grant.Id,
                Binding = terminal.Binding,
            })).ToArray(), null);
    }

    public async Task<bool> RevalidateAsync(ActivationEvidence evidence, PositionPrincipal position, CancellationToken ct)
    {
        if (evidence.UserId is not { } userId || evidence.GrantId is not { } grantId)
            return false;
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is { EmailOtpEnabled: true, EmailConfirmed: true } &&
               await signInManager.CanSignInAsync(user) &&
               await PersonalTextProofSupport.GrantIsActiveAsync(session, grantId, userId, ct);
    }

    public void RegisterInvalidationHooks(IActivationInvalidationRegistry registry)
    {
        registry.Register("user-disabled", MethodId);
        registry.Register("email-otp-disabled", MethodId);
        registry.Register("position-grant-suspended", MethodId);
        registry.Register("position-grant-revoked", MethodId);
    }
}

internal static class PersonalTextProofSupport
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    internal sealed record SubjectResult(
        ApplicationUser? User,
        PositionGrant? Grant,
        ActivationChallenge? Failure);

    internal sealed record CandidateSubjectResult(
        ApplicationUser? User,
        IReadOnlyList<PositionGrant> Grants,
        ActivationChallenge? Failure);

    public static async Task<SubjectResult> ResolveSubjectAsync(
        ActivationContext context,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        CancellationToken ct)
    {
        var accountName = context.BeginInput?.AccountName?.Trim();
        if (string.IsNullOrWhiteSpace(accountName))
            return Failed("Staffing.AccountRequired", "An account name is required for this activation method.");

        var user = await userManager.FindByNameAsync(accountName);
        if (user is null && accountName.Contains('@'))
            user = await userManager.FindByEmailAsync(accountName);
        if (user is null || !user.IsActive || user.IsDeleted || !await signInManager.CanSignInAsync(user))
            return Failed("Staffing.ActivationFailed", "The account cannot activate this position.");

        var grant = (await session.Query<PositionGrant>()
                .Where(g => g.PositionPrincipalId == context.Position.Id &&
                            g.UserId == user.Id && g.Status == PositionGrantStatus.Active)
                .ToListAsync(ct))
            .FirstOrDefault();
        if (grant is null)
            return Failed("Staffing.ActivationFailed", "The account cannot activate this position.");
        if (grant.IsActivationLockedOut(DateTimeOffset.UtcNow))
            return Failed("Staffing.GrantLocked", "Too many failed attempts; this staffing grant is temporarily locked.");
        return new SubjectResult(user, grant, null);
    }

    public static async Task<SubjectResult> ReloadSubjectAsync(
        ActivationContext context,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        CancellationToken ct)
    {
        if (context.Ceremony?.SubjectUserId is not { } userId ||
            context.Ceremony.SubjectGrantId is not { } grantId)
            return Failed("Staffing.InvalidCeremony", "Invalid or expired staffing ceremony.");
        var user = await userManager.FindByIdAsync(userId.ToString());
        var grant = await session.LoadAsync<PositionGrant>(grantId, ct);
        if (user is null || grant is not { Status: PositionGrantStatus.Active } ||
            grant.UserId != userId || grant.PositionPrincipalId != context.Position.Id ||
            !user.IsActive || user.IsDeleted || !await signInManager.CanSignInAsync(user))
            return Failed("Staffing.ActivationFailed", "The account cannot activate this position.");
        if (grant.IsActivationLockedOut(DateTimeOffset.UtcNow))
            return Failed("Staffing.GrantLocked", "Too many failed attempts; this staffing grant is temporarily locked.");
        return new SubjectResult(user, grant, null);
    }

    public static async Task<CandidateSubjectResult> ResolveCandidateSubjectAsync(
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        ActivationBeginInput input,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        CancellationToken ct)
    {
        var accountName = input.AccountName?.Trim();
        if (string.IsNullOrWhiteSpace(accountName))
            return CandidateFailed(
                "Staffing.AccountRequired", "An account name is required for this activation method.");

        var user = await userManager.FindByNameAsync(accountName);
        if (user is null && accountName.Contains('@'))
            user = await userManager.FindByEmailAsync(accountName);
        if (user is null || !user.IsActive || user.IsDeleted || !await signInManager.CanSignInAsync(user))
            return CandidateFailed(
                "Staffing.ActivationFailed", "The account cannot activate a position on this terminal.");

        var positionIds = positions.Select(position => position.Id).ToArray();
        var grants = (await session.Query<PositionGrant>()
                .Where(grant => grant.UserId == user.Id && grant.Status == PositionGrantStatus.Active)
                .ToListAsync(ct))
            .Where(grant => positionIds.Contains(grant.PositionPrincipalId) &&
                            !grant.IsActivationLockedOut(DateTimeOffset.UtcNow))
            .GroupBy(grant => grant.PositionPrincipalId)
            .Select(group => group.First())
            .ToArray();
        if (grants.Length == 0)
            return CandidateFailed(
                "Staffing.ActivationFailed", "The account cannot activate a position on this terminal.");
        return new CandidateSubjectResult(user, grants, null);
    }

    public static async Task<CandidateSubjectResult> ReloadCandidateSubjectAsync(
        StaffingCeremony ceremony,
        IReadOnlyList<PositionPrincipal> positions,
        IDocumentSession session,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        CancellationToken ct)
    {
        if (ceremony.SubjectUserId is not { } userId)
            return CandidateFailed(
                "Staffing.InvalidCeremony", "Invalid or expired staffing ceremony.");
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.IsDeleted || !await signInManager.CanSignInAsync(user))
            return CandidateFailed(
                "Staffing.ActivationFailed", "The account cannot activate a position on this terminal.");

        var positionIds = positions.Select(position => position.Id).ToArray();
        var grants = (await session.Query<PositionGrant>()
                .Where(grant => grant.UserId == userId && grant.Status == PositionGrantStatus.Active)
                .ToListAsync(ct))
            .Where(grant => positionIds.Contains(grant.PositionPrincipalId) &&
                            !grant.IsActivationLockedOut(DateTimeOffset.UtcNow))
            .GroupBy(grant => grant.PositionPrincipalId)
            .Select(group => group.First())
            .ToArray();
        if (grants.Length == 0)
            return CandidateFailed(
                "Staffing.ActivationFailed", "The account cannot activate a position on this terminal.");
        return new CandidateSubjectResult(user, grants, null);
    }

    public static async Task<StaffingCeremony> CreateCeremonyAsync(
        ActivationContext context,
        string methodId,
        ApplicationUser user,
        PositionGrant grant,
        IDocumentSession session,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        session.DeleteWhere<StaffingCeremony>(c => c.ExpiresAt < now);
        var ceremony = new StaffingCeremony
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = context.Position.Id,
            TerminalEnrollmentId = context.Terminal.Id,
            ClientId = context.Terminal.ClientId,
            DpopJkt = context.Terminal.DpopJkt ?? string.Empty,
            MethodId = methodId,
            SubjectUserId = user.Id,
            SubjectGrantId = grant.Id,
            OptionsJson = "{}",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);
        return ceremony;
    }

    public static async Task<StaffingCeremony> CreateCandidateCeremonyAsync(
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        string methodId,
        ApplicationUser user,
        IDocumentSession session,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        session.DeleteWhere<StaffingCeremony>(c => c.ExpiresAt < now);
        var ceremony = new StaffingCeremony
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = Guid.Empty,
            CandidatePositionIds = positions.Select(position => position.Id).Distinct().ToArray(),
            TerminalEnrollmentId = terminal.Id,
            ClientId = terminal.ClientId,
            DpopJkt = terminal.DpopJkt ?? string.Empty,
            MethodId = methodId,
            SubjectUserId = user.Id,
            OptionsJson = "{}",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);
        return ceremony;
    }

    public static string? ReadSecret(string response, string property)
    {
        try
        {
            using var json = JsonDocument.Parse(response);
            return json.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task RecordFailureAsync(
        IDocumentSession session, PositionGrant grant, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var lockedUntil = grant.ActivationFailedCount + 1 >= MaxFailedAttempts
            ? now + LockoutDuration
            : (DateTimeOffset?)null;
        session.Events.Append(grant.Id, new PositionGrantActivationFailed(grant.Id, now, lockedUntil));
        await session.SaveChangesAsync(ct);
    }

    public static async Task RecordSuccessAsync(
        IDocumentSession session, PositionGrant grant, CancellationToken ct)
    {
        if (grant.ActivationFailedCount == 0 && grant.ActivationLockoutEnd is null) return;
        session.Events.Append(grant.Id, new PositionGrantActivationSucceeded(grant.Id, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(ct);
    }

    public static async Task RecordFailuresAsync(
        IDocumentSession session,
        IReadOnlyList<PositionGrant> grants,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var grant in grants)
        {
            var lockedUntil = grant.ActivationFailedCount + 1 >= MaxFailedAttempts
                ? now + LockoutDuration
                : (DateTimeOffset?)null;
            session.Events.Append(grant.Id,
                new PositionGrantActivationFailed(grant.Id, now, lockedUntil));
        }
        await session.SaveChangesAsync(ct);
    }

    public static async Task RecordSuccessesAsync(
        IDocumentSession session,
        IReadOnlyList<PositionGrant> grants,
        CancellationToken ct)
    {
        var reset = grants.Where(grant =>
            grant.ActivationFailedCount != 0 || grant.ActivationLockoutEnd is not null).ToArray();
        if (reset.Length == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var grant in reset)
            session.Events.Append(grant.Id, new PositionGrantActivationSucceeded(grant.Id, now));
        await session.SaveChangesAsync(ct);
    }

    public static async Task<bool> GrantIsActiveAsync(
        IDocumentSession session, Guid grantId, Guid userId, CancellationToken ct)
    {
        var grant = await session.LoadAsync<PositionGrant>(grantId, ct);
        return grant is { Status: PositionGrantStatus.Active } && grant.UserId == userId;
    }

    public static Guid PasswordCredentialVersion(string? securityStamp)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp ?? string.Empty));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static SubjectResult Failed(string code, string message) =>
        new(null, null, ActivationChallenge.Failed(code, message));

    private static CandidateSubjectResult CandidateFailed(string code, string message) =>
        new(null, [], ActivationChallenge.Failed(code, message));
}

public sealed record ActivationResult(ActivationEvidence? Evidence, ActivationProofFailure? Failure)
{
    public static ActivationResult Failed(string code, string message) =>
        new(null, new ActivationProofFailure(code, message));
}

public sealed record CandidateActivationResult(
    IReadOnlyList<StaffingCandidateEvidence> Candidates,
    ActivationProofFailure? Failure)
{
    public static CandidateActivationResult Failed(string code, string message) =>
        new([], new ActivationProofFailure(code, message));
}

public sealed record ActivationProofFailure(string Code, string Message);

public interface IActivationInvalidationRegistry
{
    void Register(string lifecycleEvent, string methodId);
}

public sealed class ActivationInvalidationRegistry : IActivationInvalidationRegistry
{
    private readonly Dictionary<string, HashSet<string>> _methodsByEvent = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> MethodsFor(string lifecycleEvent) =>
        _methodsByEvent.TryGetValue(lifecycleEvent, out var methods) ? methods : [];

    public void Register(string lifecycleEvent, string methodId)
    {
        if (!_methodsByEvent.TryGetValue(lifecycleEvent, out var methods))
            _methodsByEvent[lifecycleEvent] = methods = new HashSet<string>(StringComparer.Ordinal);
        methods.Add(methodId);
    }
}

public sealed class ActivationProofRegistry
{
    private readonly IReadOnlyDictionary<string, IActivationProof> _proofs;

    public ActivationProofRegistry(
        IEnumerable<IActivationProof> proofs,
        ActivationInvalidationRegistry invalidations)
    {
        _proofs = proofs.ToDictionary(p => p.MethodId, StringComparer.Ordinal);
        foreach (var proof in _proofs.Values)
        {
            if (!ActivationProofMethodIds.Known.TryGetValue(proof.MethodId, out var descriptor) ||
                !descriptor.IsAvailable ||
                descriptor.Capabilities != proof.Capabilities ||
                descriptor.OwnerKind != proof.OwnerKind)
            {
                throw new InvalidOperationException(
                    $"Activation proof '{proof.MethodId}' does not match its immutable security descriptor.");
            }
            proof.RegisterInvalidationHooks(invalidations);
        }
    }

    public bool TryGet(string methodId, out IActivationProof proof) =>
        _proofs.TryGetValue(methodId, out proof!);
}

/// <summary>WebAuthn proof owned by a logical position token. Unlike a person
/// passkey it establishes possession of an assigned team credential and never
/// invents a human actor.</summary>
public sealed class PositionTokenActivationProof(
    IDocumentSession session,
    RealmScopedFido2Factory fido2Factory) : IActivationProof
{
    public string MethodId => ActivationProofMethodIds.PositionToken;
    public ProofCapability Capabilities =>
        ProofCapability.PhishingResistant | ProofCapability.IndividuallyRevocable;
    public ActivationProofOwnerKind OwnerKind => ActivationProofOwnerKind.PositionCredential;

    public async Task<ActivationChallenge> BeginAsync(ActivationContext context, CancellationToken ct)
    {
        var tokens = (await session.Query<ActivationToken>()
                .Where(t => t.Status == ActivationTokenStatus.Active)
                .ToListAsync(ct))
            .Where(t => t.AssignedPositionIds.Contains(context.Position.Id))
            .Select(t => t.Id)
            .ToHashSet();
        var credentials = (await session.Query<ActivationTokenCredential>()
                .Where(c => c.RpId == context.Terminal.WebAuthnRpId)
                .ToListAsync(ct))
            .Where(c => tokens.Contains(c.ActivationTokenId))
            .ToList();
        if (credentials.Count == 0)
            return ActivationChallenge.Failed(
                "Staffing.NoEligiblePositionTokens",
                "No assigned position token is registered for this terminal's relying party.");

        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: context.Terminal.WebAuthnRpId);
        }
        catch (RelyingPartyUnavailableException)
        {
            return ActivationChallenge.Failed(
                "Staffing.RelyingPartyUnavailable", "The terminal's relying party is not available.");
        }

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials
                .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
                .ToList(),
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var optionsJson = options.ToJson();
        var now = DateTimeOffset.UtcNow;
        session.DeleteWhere<StaffingCeremony>(c => c.ExpiresAt < now);
        var ceremony = new StaffingCeremony
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = context.Position.Id,
            TerminalEnrollmentId = context.Terminal.Id,
            ClientId = context.Terminal.ClientId,
            DpopJkt = context.Terminal.DpopJkt ?? string.Empty,
            MethodId = MethodId,
            RpId = context.Terminal.WebAuthnRpId,
            OptionsJson = optionsJson,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);
        return new ActivationChallenge(ceremony, optionsJson, null);
    }

    public async Task<ActivationResult> CompleteAsync(
        ActivationContext context, string response, CancellationToken ct)
    {
        if (context.Ceremony is not { } ceremony)
            return ActivationResult.Failed("Staffing.InvalidCeremony", "Invalid or expired staffing ceremony.");

        string[]? presentedOrigins = null;
        try
        {
            var assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (RealmFido2.TryGetClientDataOrigin(assertion?.Response?.ClientDataJson) is { } origin)
                presentedOrigins = [origin];
        }
        catch (JsonException) { }

        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(
                ct, rpIdOverride: ceremony.RpId, additionalOrigins: presentedOrigins);
        }
        catch (RelyingPartyUnavailableException)
        {
            return ActivationResult.Failed(
                "Staffing.RelyingPartyUnavailable", "Staffing is not available for this realm.");
        }

        AssertionOptions options;
        try { options = AssertionOptions.FromJson(ceremony.OptionsJson); }
        catch { return ActivationResult.Failed("Staffing.InvalidCeremony", "Invalid or expired staffing ceremony."); }

        var credential = await ActivationTokenAssertionVerifier.VerifyAsync(
            fido2, options, response, session, ceremony.RpId, ct);
        if (credential is null)
            return ActivationResult.Failed("Staffing.PositionTokenFailed", "Position token verification failed.");

        var token = await session.LoadAsync<ActivationToken>(credential.ActivationTokenId, ct);
        if (token is not { Status: ActivationTokenStatus.Active } ||
            !token.AssignedPositionIds.Contains(context.Position.Id))
            return ActivationResult.Failed("Staffing.PositionTokenFailed", "Position token verification failed.");

        return new ActivationResult(new ActivationEvidence
        {
            MethodId = MethodId,
            CredentialId = credential.Id,
            ActivationTokenId = token.Id,
            Binding = context.Terminal.Binding,
        }, null);
    }

    public async Task<ActivationChallenge> BeginCandidatesAsync(
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        ActivationBeginInput input,
        CancellationToken ct)
    {
        var positionIds = positions.Select(position => position.Id).ToArray();
        var tokens = (await session.Query<ActivationToken>()
                .Where(token => token.Status == ActivationTokenStatus.Active)
                .ToListAsync(ct))
            .Where(token => token.AssignedPositionIds.Any(positionIds.Contains))
            .ToArray();
        var tokenIds = tokens.Select(token => token.Id).ToHashSet();
        var credentials = (await session.Query<ActivationTokenCredential>()
                .Where(credential => credential.RpId == terminal.WebAuthnRpId)
                .ToListAsync(ct))
            .Where(credential => tokenIds.Contains(credential.ActivationTokenId))
            .ToList();
        if (credentials.Count == 0)
            return ActivationChallenge.Failed(
                "Staffing.NoEligiblePositionTokens",
                "No assigned position token is registered for this terminal's relying party.");

        IFido2 fido2;
        try { fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: terminal.WebAuthnRpId); }
        catch (RelyingPartyUnavailableException)
        {
            return ActivationChallenge.Failed(
                "Staffing.RelyingPartyUnavailable", "The terminal's relying party is not available.");
        }
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials
                .Select(credential => new PublicKeyCredentialDescriptor(credential.CredentialId))
                .ToList(),
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var optionsJson = options.ToJson();
        var now = DateTimeOffset.UtcNow;
        session.DeleteWhere<StaffingCeremony>(ceremony => ceremony.ExpiresAt < now);
        var ceremony = new StaffingCeremony
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = Guid.Empty,
            CandidatePositionIds = positions.Select(position => position.Id).Distinct().ToArray(),
            TerminalEnrollmentId = terminal.Id,
            ClientId = terminal.ClientId,
            DpopJkt = terminal.DpopJkt ?? string.Empty,
            MethodId = MethodId,
            RpId = terminal.WebAuthnRpId,
            OptionsJson = optionsJson,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);
        return new ActivationChallenge(ceremony, optionsJson, null);
    }

    public async Task<CandidateActivationResult> CompleteCandidatesAsync(
        StaffingCeremony ceremony,
        string response,
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        CancellationToken ct)
    {
        string[]? presentedOrigins = null;
        try
        {
            var assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (RealmFido2.TryGetClientDataOrigin(assertion?.Response?.ClientDataJson) is { } origin)
                presentedOrigins = [origin];
        }
        catch (JsonException) { }

        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(
                ct, rpIdOverride: ceremony.RpId, additionalOrigins: presentedOrigins);
        }
        catch (RelyingPartyUnavailableException)
        {
            return CandidateActivationResult.Failed(
                "Staffing.RelyingPartyUnavailable", "Staffing is not available for this realm.");
        }
        AssertionOptions options;
        try { options = AssertionOptions.FromJson(ceremony.OptionsJson); }
        catch
        {
            return CandidateActivationResult.Failed(
                "Staffing.InvalidCeremony", "Invalid or expired staffing ceremony.");
        }
        var credential = await ActivationTokenAssertionVerifier.VerifyAsync(
            fido2, options, response, session, ceremony.RpId, ct);
        if (credential is null)
            return CandidateActivationResult.Failed(
                "Staffing.PositionTokenFailed", "Position token verification failed.");
        var token = await session.LoadAsync<ActivationToken>(credential.ActivationTokenId, ct);
        if (token is not { Status: ActivationTokenStatus.Active })
            return CandidateActivationResult.Failed(
                "Staffing.PositionTokenFailed", "Position token verification failed.");

        var allowed = positions.Select(position => position.Id).ToHashSet();
        var candidates = token.AssignedPositionIds
            .Where(allowed.Contains)
            .Distinct()
            .Select(positionId => new StaffingCandidateEvidence(positionId, new ActivationEvidence
            {
                MethodId = MethodId,
                CredentialId = credential.Id,
                ActivationTokenId = token.Id,
                Binding = terminal.Binding,
            }))
            .ToArray();
        return candidates.Length == 0
            ? CandidateActivationResult.Failed(
                "Staffing.PositionTokenFailed", "Position token verification failed.")
            : new CandidateActivationResult(candidates, null);
    }

    public async Task<bool> RevalidateAsync(
        ActivationEvidence evidence, PositionPrincipal position, CancellationToken ct)
    {
        if (evidence.ActivationTokenId is not { } tokenId || evidence.CredentialId is not { } credentialId)
            return false;
        var token = await session.LoadAsync<ActivationToken>(tokenId, ct);
        if (token is not { Status: ActivationTokenStatus.Active } ||
            !token.AssignedPositionIds.Contains(position.Id))
            return false;
        var credential = await session.LoadAsync<ActivationTokenCredential>(credentialId, ct);
        return credential?.ActivationTokenId == tokenId;
    }

    public void RegisterInvalidationHooks(IActivationInvalidationRegistry registry)
    {
        registry.Register("activation-token-revoked", MethodId);
        registry.Register("activation-token-unassigned", MethodId);
    }
}

internal static class ActivationTokenAssertionVerifier
{
    public static async Task<ActivationTokenCredential?> VerifyAsync(
        IFido2 fido2,
        AssertionOptions originalOptions,
        string assertionJson,
        IDocumentSession session,
        string activeRpId,
        CancellationToken ct)
    {
        AuthenticatorAssertionRawResponse? assertion;
        try
        {
            assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                assertionJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return null; }
        if (assertion is null || string.IsNullOrEmpty(assertion.Id) || assertion.Response is null) return null;

        byte[] credentialId;
        try
        {
            credentialId = Convert.FromBase64String(assertion.Id.Replace('-', '+').Replace('_', '/')
                .PadRight(assertion.Id.Length + (4 - assertion.Id.Length % 4) % 4, '='));
        }
        catch (FormatException) { return null; }

        var candidates = await session.Query<ActivationTokenCredential>()
            .Where(c => c.RpId == activeRpId)
            .ToListAsync(ct);
        var stored = candidates.FirstOrDefault(c => c.CredentialId.SequenceEqual(credentialId));
        if (stored is null) return null;

        VerifyAssertionResult verified;
        try
        {
            verified = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertion,
                OriginalOptions = originalOptions,
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = stored.SignatureCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                    candidates.Any(c => c.CredentialId.SequenceEqual(args.CredentialId) &&
                                        c.UserHandle.SequenceEqual(args.UserHandle))),
            }, ct);
        }
        catch { return null; }

        stored.SignatureCount = verified.SignCount;
        stored.LastUsedAt = DateTimeOffset.UtcNow;
        session.Store(stored);
        await session.SaveChangesAsync(ct);
        return stored;
    }
}

/// <summary>Reference adapter for the existing staffing passkey flow. This is
/// intentionally a refactoring of the prior inline branch, not a new login
/// implementation.</summary>
public sealed class PersonalPasskeyActivationProof(
    IDocumentSession session,
    RealmScopedFido2Factory fido2Factory,
    RpIdResolver rpIdResolver,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) : IActivationProof
{
    public string MethodId => ActivationProofMethodIds.PersonalPasskey;
    public ProofCapability Capabilities =>
        ProofCapability.IdentifiedActor |
        ProofCapability.PhishingResistant |
        ProofCapability.IndividuallyRevocable;
    public ActivationProofOwnerKind OwnerKind => ActivationProofOwnerKind.Personal;

    public async Task<ActivationChallenge> BeginAsync(ActivationContext context, CancellationToken ct)
    {
        var grantedUserIds = (await session.Query<PositionGrant>()
                .Where(g => g.PositionPrincipalId == context.Position.Id &&
                            g.Status == PositionGrantStatus.Active)
                .ToListAsync(ct))
            .Select(g => g.UserId)
            .Distinct()
            .ToList();
        if (grantedUserIds.Count == 0)
            return ActivationChallenge.Failed(
                "Staffing.NoActiveGrants", "No user is authorized to staff this position.");

        var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
        var allowedCredentials = (await session.Query<StoredPasskeyCredential>()
                .Where(c => grantedUserIds.Contains(c.UserId))
                .ToListAsync(ct))
            .Where(c => string.Equals(c.RpId ?? primaryDomain, context.Terminal.WebAuthnRpId,
                StringComparison.OrdinalIgnoreCase))
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();
        if (allowedCredentials.Count == 0)
            return ActivationChallenge.Failed(
                "Staffing.NoEligiblePasskeys", "No authorized user has a passkey for this terminal.");

        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: context.Terminal.WebAuthnRpId);
        }
        catch (RelyingPartyUnavailableException)
        {
            return ActivationChallenge.Failed(
                "Staffing.RelyingPartyUnavailable", "The terminal's relying party is not available.");
        }

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials,
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var optionsJson = options.ToJson();

        session.DeleteWhere<StaffingCeremony>(c => c.ExpiresAt < DateTimeOffset.UtcNow);
        var ceremony = new StaffingCeremony
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = context.Position.Id,
            TerminalEnrollmentId = context.Terminal.Id,
            ClientId = context.Terminal.ClientId,
            DpopJkt = context.Terminal.DpopJkt ?? string.Empty,
            MethodId = MethodId,
            RpId = context.Terminal.WebAuthnRpId,
            OptionsJson = optionsJson,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);
        return new ActivationChallenge(ceremony, optionsJson, null);
    }

    public async Task<ActivationResult> CompleteAsync(
        ActivationContext context, string response, CancellationToken ct)
    {
        if (context.Ceremony is not { } ceremony)
            return ActivationResult.Failed("Staffing.InvalidCeremony", "Invalid or expired staffing ceremony.");

        string[]? presentedOrigins = null;
        try
        {
            var assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (RealmFido2.TryGetClientDataOrigin(assertion?.Response?.ClientDataJson) is { } origin)
                presentedOrigins = [origin];
        }
        catch (JsonException) { }

        var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(
                ct, rpIdOverride: ceremony.RpId, additionalOrigins: presentedOrigins);
        }
        catch (RelyingPartyUnavailableException)
        {
            return ActivationResult.Failed(
                "Staffing.RelyingPartyUnavailable", "Staffing is not available for this realm.");
        }

        AssertionOptions options;
        try
        {
            options = AssertionOptions.FromJson(ceremony.OptionsJson);
        }
        catch
        {
            return ActivationResult.Failed("Staffing.InvalidCeremony", "Invalid or expired staffing ceremony.");
        }

        var storedCredential = await PasskeyAssertionVerifier.VerifyAsync(
            fido2, options, response, session, ceremony.RpId, primaryDomain, ct);
        if (storedCredential is null)
            return ActivationResult.Failed("Staffing.PasskeyFailed", "Passkey verification failed.");

        var user = await userManager.FindByIdAsync(storedCredential.UserId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted ||
            !string.Equals(storedCredential.RpId ?? primaryDomain, ceremony.RpId,
                StringComparison.OrdinalIgnoreCase))
        {
            return ActivationResult.Failed("Staffing.PasskeyFailed", "Passkey verification failed.");
        }

        var grant = (await session.Query<PositionGrant>()
                .Where(g => g.PositionPrincipalId == context.Position.Id && g.UserId == user.Id &&
                            g.Status == PositionGrantStatus.Active)
                .ToListAsync(ct))
            .FirstOrDefault();
        if (grant is null)
            return ActivationResult.Failed(
                "Staffing.GrantRequired", "The user is not authorized to staff this position.");

        return new ActivationResult(new ActivationEvidence
        {
            MethodId = MethodId,
            UserId = user.Id,
            GrantId = grant.Id,
            CredentialId = storedCredential.Id,
            Binding = context.Terminal.Binding,
        }, null);
    }

    public async Task<ActivationChallenge> BeginCandidatesAsync(
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        ActivationBeginInput input,
        CancellationToken ct)
    {
        var positionIds = positions.Select(position => position.Id).ToArray();
        var grantedUserIds = (await session.Query<PositionGrant>()
                .Where(grant => grant.Status == PositionGrantStatus.Active)
                .ToListAsync(ct))
            .Where(grant => positionIds.Contains(grant.PositionPrincipalId))
            .Select(grant => grant.UserId)
            .Distinct()
            .ToList();
        if (grantedUserIds.Count == 0)
            return ActivationChallenge.Failed(
                "Staffing.NoActiveGrants", "No user is authorized to staff a position on this terminal.");

        var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
        var credentials = (await session.Query<StoredPasskeyCredential>()
                .Where(credential => grantedUserIds.Contains(credential.UserId))
                .ToListAsync(ct))
            .Where(credential => string.Equals(
                credential.RpId ?? primaryDomain, terminal.WebAuthnRpId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (credentials.Count == 0)
            return ActivationChallenge.Failed(
                "Staffing.NoEligiblePasskeys", "No authorized user has a passkey for this terminal.");

        IFido2 fido2;
        try { fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: terminal.WebAuthnRpId); }
        catch (RelyingPartyUnavailableException)
        {
            return ActivationChallenge.Failed(
                "Staffing.RelyingPartyUnavailable", "The terminal's relying party is not available.");
        }
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials
                .Select(credential => new PublicKeyCredentialDescriptor(credential.CredentialId))
                .ToList(),
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var optionsJson = options.ToJson();
        var now = DateTimeOffset.UtcNow;
        session.DeleteWhere<StaffingCeremony>(ceremony => ceremony.ExpiresAt < now);
        var ceremony = new StaffingCeremony
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = Guid.Empty,
            CandidatePositionIds = positions.Select(position => position.Id).Distinct().ToArray(),
            TerminalEnrollmentId = terminal.Id,
            ClientId = terminal.ClientId,
            DpopJkt = terminal.DpopJkt ?? string.Empty,
            MethodId = MethodId,
            RpId = terminal.WebAuthnRpId,
            OptionsJson = optionsJson,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);
        return new ActivationChallenge(ceremony, optionsJson, null);
    }

    public async Task<CandidateActivationResult> CompleteCandidatesAsync(
        StaffingCeremony ceremony,
        string response,
        IReadOnlyList<PositionPrincipal> positions,
        TerminalEnrollment terminal,
        CancellationToken ct)
    {
        string[]? presentedOrigins = null;
        try
        {
            var assertion = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (RealmFido2.TryGetClientDataOrigin(assertion?.Response?.ClientDataJson) is { } origin)
                presentedOrigins = [origin];
        }
        catch (JsonException) { }

        var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(
                ct, rpIdOverride: ceremony.RpId, additionalOrigins: presentedOrigins);
        }
        catch (RelyingPartyUnavailableException)
        {
            return CandidateActivationResult.Failed(
                "Staffing.RelyingPartyUnavailable", "Staffing is not available for this realm.");
        }
        AssertionOptions options;
        try { options = AssertionOptions.FromJson(ceremony.OptionsJson); }
        catch
        {
            return CandidateActivationResult.Failed(
                "Staffing.InvalidCeremony", "Invalid or expired staffing ceremony.");
        }
        var credential = await PasskeyAssertionVerifier.VerifyAsync(
            fido2, options, response, session, ceremony.RpId, primaryDomain, ct);
        if (credential is null)
            return CandidateActivationResult.Failed(
                "Staffing.PasskeyFailed", "Passkey verification failed.");
        var user = await userManager.FindByIdAsync(credential.UserId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted)
            return CandidateActivationResult.Failed(
                "Staffing.PasskeyFailed", "Passkey verification failed.");

        var positionIds = positions.Select(position => position.Id).ToArray();
        var grants = (await session.Query<PositionGrant>()
                .Where(grant => grant.UserId == user.Id && grant.Status == PositionGrantStatus.Active)
                .ToListAsync(ct))
            .Where(grant => positionIds.Contains(grant.PositionPrincipalId))
            .GroupBy(grant => grant.PositionPrincipalId)
            .Select(group => group.First())
            .ToArray();
        if (grants.Length == 0)
            return CandidateActivationResult.Failed(
                "Staffing.GrantRequired", "The user is not authorized to staff a position on this terminal.");
        return new CandidateActivationResult(grants.Select(grant =>
            new StaffingCandidateEvidence(grant.PositionPrincipalId, new ActivationEvidence
            {
                MethodId = MethodId,
                UserId = user.Id,
                GrantId = grant.Id,
                CredentialId = credential.Id,
                Binding = terminal.Binding,
            })).ToArray(), null);
    }

    public async Task<bool> RevalidateAsync(ActivationEvidence evidence, PositionPrincipal position, CancellationToken ct)
    {
        if (evidence.UserId is not { } userId || evidence.CredentialId is not { } credentialId ||
            evidence.GrantId is not { } grantId)
            return false;

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || !user.IsActive || user.IsDeleted)
            return false;

        var credential = await session.LoadAsync<StoredPasskeyCredential>(credentialId, ct);
        if (credential is null || credential.UserId != userId) return false;

        var grant = await session.LoadAsync<PositionGrant>(grantId, ct);
        return grant is { Status: PositionGrantStatus.Active, UserId: var grantUserId }
               && grantUserId == userId;
    }

    public void RegisterInvalidationHooks(IActivationInvalidationRegistry registry)
    {
        registry.Register("user-disabled", MethodId);
        registry.Register("passkey-deleted", MethodId);
        registry.Register("position-grant-suspended", MethodId);
        registry.Register("position-grant-revoked", MethodId);
    }
}
