using System.Collections.Concurrent;
using System.Net;

namespace Modgud.Api.Tests.Infrastructure;

/// <summary>
/// ADR 0021 — stands in for every relying party's back-channel logout endpoint. The
/// delivery worker's named <c>HttpClient</c> gets this as its primary handler, so no
/// socket is opened: every POST is recorded here and answered with the status the test
/// configured for that URI (200 by default). Singleton; the client factory's handler
/// recycling must not dispose it.
/// </summary>
public sealed class RecordingBackChannelLogoutSink : HttpMessageHandler
{
    public sealed record Delivery(Uri Target, string LogoutToken, string? CacheControl, DateTimeOffset At);

    private readonly ConcurrentQueue<Delivery> _deliveries = new();
    private readonly ConcurrentDictionary<string, HttpStatusCode> _responses = new(StringComparer.Ordinal);

    public IReadOnlyList<Delivery> Deliveries => [.. _deliveries];

    /// <summary>Answer every POST to <paramref name="uri"/> with <paramref name="status"/>.</summary>
    public void Respond(string uri, HttpStatusCode status) => _responses[uri] = status;

    public void Reset()
    {
        _deliveries.Clear();
        _responses.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = request.Content is null
            ? []
            : System.Web.HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(cancellationToken));
        var token = form["logout_token"] ?? string.Empty;
        _deliveries.Enqueue(new Delivery(
            request.RequestUri!,
            token,
            request.Headers.CacheControl?.ToString(),
            DateTimeOffset.UtcNow));

        var status = _responses.TryGetValue(request.RequestUri!.ToString(), out var configured)
            ? configured
            : HttpStatusCode.OK;
        return new HttpResponseMessage(status) { RequestMessage = request };
    }

    /// <summary>Waits until at least <paramref name="count"/> deliveries hit <paramref name="uri"/>.</summary>
    public async Task<IReadOnlyList<Delivery>> WaitForAsync(string uri, int count, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var hits = _deliveries.Where(d => d.Target.ToString() == uri).ToList();
            if (hits.Count >= count) return hits;
            if (DateTimeOffset.UtcNow > deadline) return hits;
            await Task.Delay(100);
        }
    }

    protected override void Dispose(bool disposing)
    {
        // Intentionally kept alive across HttpClientFactory handler rotations.
    }
}
