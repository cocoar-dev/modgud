using Modgud.Authentication.Setup;
using Modgud.Domain.Realms;
using ErrorOr;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// Test-controllable fault switch for the cold-start harness. Registered as a
/// singleton on <see cref="ColdStartWebApplicationFactory"/>; a test flips
/// <see cref="ThrowOnInvite"/> to simulate the initial-admin invite blowing up
/// AFTER the realm was already provisioned — i.e. the non-atomic realm-create
/// path (<c>RealmsEndpoints</c> POST issues the bootstrap-invite after
/// <c>CreateRealmAsync</c> has already committed the realm + its tenant DB).
///
/// <para>It is a singleton (not AsyncLocal) on purpose: the
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> handles the request on
/// its own execution context, so an AsyncLocal flag set by the test caller would
/// not flow into the request pipeline. A shared singleton crosses that boundary.
/// The cold-start collection runs without parallelization, so there is no race.</para>
/// </summary>
public sealed class ColdStartFaultInjection
{
    /// <summary>When true, the next <c>IssueAsync</c> throws instead of issuing an invite.</summary>
    public bool ThrowOnInvite { get; set; }

    /// <summary>Message used for the injected failure.</summary>
    public string FailureMessage { get; set; } = "Injected bootstrap-invite failure (cold-start test).";

    public void Reset() => ThrowOnInvite = false;
}

/// <summary>
/// Decorator over the real <see cref="IPendingAdminInviteService"/> that throws
/// from <see cref="IssueAsync"/> when <see cref="ColdStartFaultInjection.ThrowOnInvite"/>
/// is set. Everything else delegates to the real service so the happy path and
/// the consume path are unchanged.
/// </summary>
internal sealed class FaultInjectingPendingAdminInviteService(
    PendingAdminInviteService inner,
    ColdStartFaultInjection fault) : IPendingAdminInviteService
{
    public Task<IssuedInvite> IssueAsync(
        string userName,
        string email,
        string? firstname,
        string? lastname,
        string? issuedBy,
        Realm realm,
        CancellationToken ct = default)
    {
        if (fault.ThrowOnInvite)
            throw new InvalidOperationException(fault.FailureMessage);

        return inner.IssueAsync(userName, email, firstname, lastname, issuedBy, realm, ct);
    }

    public Task<ErrorOr<BootstrappedAdmin>> ConsumeAsync(
        string plaintextToken,
        string password,
        CancellationToken ct = default)
        => inner.ConsumeAsync(plaintextToken, password, ct);
}
