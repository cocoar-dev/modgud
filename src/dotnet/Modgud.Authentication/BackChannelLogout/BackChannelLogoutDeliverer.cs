using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Events;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Observability;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.BackChannelLogout;

/// <summary>ADR 0009 transport A — attempts pending logout-token deliveries.</summary>
public interface IBackChannelLogoutDeliverer
{
    /// <summary>One attempt for one pending delivery in the given (tenant-scoped) unit of
    /// work: claims it, mints a fresh logout token, POSTs it, records the outcome. Returns
    /// <c>false</c> when the row was claimed by someone else meanwhile.</summary>
    Task<bool> AttemptAsync(IDocumentSession session, BackChannelLogoutDelivery delivery, CancellationToken ct = default);

    /// <summary>Attempts every delivery of the current realm whose next attempt is due.
    /// Returns (attempted, delivered).</summary>
    Task<(int Attempted, int Delivered)> SweepDueAsync(CancellationToken ct = default);
}

public sealed class BackChannelLogoutDeliverer(
    IDocumentSession session,
    LogoutTokenMinter minter,
    IHttpClientFactory httpClients,
    ISecurityAuditLog audit,
    TimeProvider clock,
    ILogger<BackChannelLogoutDeliverer> logger) : IBackChannelLogoutDeliverer
{
    public async Task<(int Attempted, int Delivered)> SweepDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await session.Query<BackChannelLogoutDelivery>()
            .Where(d => d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(200)
            .ToListAsync(ct);

        var attempted = 0;
        var delivered = 0;
        foreach (var delivery in due)
        {
            if (!await AttemptAsync(session, delivery, ct)) continue;
            attempted++;
            if (delivery.LastOutcome == BackChannelLogoutDeliveryStatus.Delivered) delivered++;
        }
        return (attempted, delivered);
    }

    public async Task<bool> AttemptAsync(IDocumentSession work, BackChannelLogoutDelivery delivery, CancellationToken ct = default)
    {
        var realm = TenantContext.Current;
        var now = clock.GetUtcNow();

        var client = await work.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(a => a.ClientId == delivery.ClientId && !a.IsDeleted, ct);
        if (client is null
            || !client.Settings.TryGetValue(OAuthApplicationSettingKeys.BackChannelLogoutUri, out var uri)
            || string.IsNullOrWhiteSpace(uri))
        {
            // The URI was removed (or the client deleted) meanwhile: nothing left to notify.
            logger.LogInformation("Back-channel logout for client {ClientId} dropped: no logout URI registered any more", delivery.ClientId);
            work.Delete(delivery);
            await work.SaveChangesAsync(ct);
            return true;
        }

        // Claim the attempt (version-checked): whoever saves first sends; the other skips.
        var attempt = delivery.Attempts + 1;
        var last = attempt > BackChannelLogoutConstants.RetrySchedule.Length;
        delivery.Attempts = attempt;
        delivery.NextAttemptAt = last
            ? DateTimeOffset.MaxValue
            : now + BackChannelLogoutConstants.RetrySchedule[attempt - 1];
        work.Store(delivery);
        try
        {
            await work.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            return false;
        }

        // Spec: sid is REQUIRED when the client requires it; it is present whenever the
        // ended scope names a session. A user-level end has none and logs out every
        // session of the subject at the RP.
        var sid = delivery.SessionId?.ToString();
        if (sid is null && delivery.Scope == AccessEndScope.Session
            && ReadBool(client.Properties, OAuthApplicationPropertyKeys.BackChannelLogoutSessionRequired, true))
            logger.LogWarning("Client {ClientId} requires sid in logout tokens but the session end carried none", delivery.ClientId);

        var token = await minter.MintAsync(realm, delivery.Issuer, delivery.ClientId, delivery.UserId.ToString(), sid, ct);

        var started = Stopwatch.GetTimestamp();
        var outcome = await PostAsync(uri, token, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var delivered = outcome == BackChannelLogoutDeliveryStatus.Delivered;

        delivery.LastOutcome = outcome;
        if (delivered || last)
            work.Delete(delivery);
        else
            work.Store(delivery);
        work.Store(new BackChannelLogoutDeliveryStatus
        {
            Id = client.Id,
            ClientId = client.ClientId,
            LastAttemptAt = clock.GetUtcNow(),
            LastOutcome = outcome,
            Attempt = attempt,
            TargetUri = uri,
        });
        await work.SaveChangesAsync(ct);

        BackChannelLogoutMetrics.Delivery(delivery.ClientId, delivered ? "delivered" : "failed", elapsed, realm);
        audit.RecordTelemetry(new SecurityAuditRecord
        {
            EventType = delivered ? AuditEvents.BackChannelLogoutSent : AuditEvents.BackChannelLogoutFailed,
            RealmSlug = realm,
            CaptureRequestContext = false,
            Severity = delivered ? AuditSeverity.Info : last ? AuditSeverity.Error : AuditSeverity.Warning,
            TargetSubjectId = delivery.UserId,
            SessionId = delivery.SessionId,
            OAuthClientId = delivery.ClientId,
            OutcomeCode = delivered ? AuditOutcomes.Succeeded : AuditOutcomes.Failed,
            ReasonCode = delivered ? delivery.Reason : outcome,
            OperationCode = delivery.Scope == AccessEndScope.User ? "user" : "session",
            Count = attempt,
        });

        if (!delivered)
            logger.LogWarning(
                "Back-channel logout to client {ClientId} failed (attempt {Attempt}{Final}): {Outcome}",
                delivery.ClientId, attempt, last ? ", giving up" : "", outcome);
        return true;
    }

    private static bool ReadBool(IDictionary<string, object?> props, string key, bool fallback)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return fallback;
        return raw switch
        {
            bool b => b,
            JsonElement e when e.ValueKind is JsonValueKind.True => true,
            JsonElement e when e.ValueKind is JsonValueKind.False => false,
            string str when bool.TryParse(str, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private async Task<string> PostAsync(string uri, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("logout_token", token)]),
            };
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(BackChannelLogoutConstants.DeliveryTimeout);
            using var response = await httpClients.CreateClient(BackChannelLogoutConstants.HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return (int)response.StatusCode is 200 or 204
                ? BackChannelLogoutDeliveryStatus.Delivered
                : $"failed:http-{(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "failed:timeout";
        }
        catch (HttpRequestException ex) when (ex.InnerException is IOException io && io.Message.Contains("refused:", StringComparison.Ordinal))
        {
            // SsrfSafeHttpHandlerFactory refused the resolved address.
            return "failed:ssrf";
        }
        catch (HttpRequestException)
        {
            return "failed:connect";
        }
        catch (IOException ex) when (ex.Message.Contains("refused:", StringComparison.Ordinal))
        {
            return "failed:ssrf";
        }
    }
}
