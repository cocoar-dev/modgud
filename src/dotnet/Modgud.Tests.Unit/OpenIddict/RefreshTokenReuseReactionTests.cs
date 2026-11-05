using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.OpenIddict;

namespace Modgud.Tests.Unit.OpenIddict;

/// <summary>
/// B9-J02 — when the required audit write of a detected refresh-token reuse failed, the
/// handler threw before its observers (which end the higher-level sessions) and before the
/// stock token-family teardown. The reaction must not depend on the audit.
/// </summary>
public sealed class RefreshTokenReuseReactionTests
{
    [Fact]
    public async Task Observers_still_run_when_the_audit_write_fails()
    {
        var observer = new RecordingObserver();
        var handler = new RefreshTokenReuseAuditHandler(
            tokenManager: null!, applicationManager: null!, new FailingAuditLog(), [observer],
            NullLogger<RefreshTokenReuseAuditHandler>.Instance);

        await handler.ReactAsync("user-1", "client-1", "auth-1", familySize: 3, CancellationToken.None);

        Assert.Equal("auth-1", observer.SeenAuthorizationId);
    }

    private sealed class RecordingObserver : IRefreshTokenReuseObserver
    {
        public string? SeenAuthorizationId { get; private set; }
        public Task OnReuseDetectedAsync(string? subject, string? clientId, string? authorizationId, CancellationToken ct)
        {
            SeenAuthorizationId = authorizationId;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingAuditLog : ISecurityAuditLog
    {
        public ValueTask RecordRequiredAsync(SecurityAuditRecord record, CancellationToken ct = default) =>
            throw new InvalidOperationException("audit store unavailable");
        public void StoreRequired(IDocumentSession session, SecurityAuditRecord record) { }
        public ValueTask RecordIncidentAsync(SecurityAuditRecord record, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void RecordAbuse(SecurityAuditRecord record) { }
        public void RecordTelemetry(SecurityAuditRecord record) { }
        public ValueTask RecordPlatformRequiredAsync(PlatformAuditRecord record, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void StorePlatformRequired(IDocumentSession session, PlatformAuditRecord record) { }
        public void RecordPlatformTelemetry(PlatformAuditRecord record) { }
    }
}
