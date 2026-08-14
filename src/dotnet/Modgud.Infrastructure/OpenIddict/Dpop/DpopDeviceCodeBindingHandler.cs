using System.Security.Cryptography;
using System.Text;
using Marten;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.Persistence.Tenancy;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace Modgud.Infrastructure.OpenIddict.Dpop;

/// <summary>
/// Enforces the DPoP binding of a device code when it is polled (MG-FT spike —
/// RFC 9449 applied to the RFC 8628 device grant): a device code minted from a
/// DPoP-proofed device-authorization request may only be redeemed with a proof
/// of the SAME key, so a leaked device/user code is useless to anyone who does
/// not hold the requesting device's private key.
///
/// <para>The binding lives in a <see cref="DeviceCodeDpopBinding"/> companion
/// document written by <see cref="DpopDeviceCodeBindingCaptureHandler"/> — NOT
/// inside the device code: OpenIddict builds the initial device-code payload
/// internally and regenerates it at the end-user approval, so a claim stamped
/// into the token does not survive (verified empirically in the spike). The
/// ledger is keyed by SHA-256(device_code) and is independent of both steps.</para>
///
/// <para>Runs BEFORE <c>RedeemTokenEntry</c> (unlike the refresh enforcement):
/// a mismatched or missing proof rejects the poll WITHOUT consuming the device
/// code, so the legitimate device keeps polling and still gets its tokens. An
/// unbound device code (no proof at the device request → no ledger row) is
/// unaffected.</para>
/// </summary>
public sealed class DpopDeviceCodeBindingHandler : IOpenIddictServerHandler<ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessSignInContext>()
            .UseScopedHandler<DpopDeviceCodeBindingHandler>()
            // After DpopProofValidationHandler (RedeemTokenEntry.Order - 2) has
            // stashed the polling proof's thumbprint, still before RedeemTokenEntry
            // consumes the device code — a rejected poll must not burn it.
            .SetOrder(RedeemTokenEntry.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly ITenantSessionFactory _sessionFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public DpopDeviceCodeBindingHandler(
        ITenantSessionFactory sessionFactory, IHttpContextAccessor httpContextAccessor)
    {
        _sessionFactory = sessionFactory;
        _httpContextAccessor = httpContextAccessor;
    }

    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EndpointType != OpenIddictServerEndpointType.Token)
            return;
        if (context.Request?.IsDeviceCodeGrantType() != true)
            return;

        var deviceCode = context.Request.DeviceCode;
        if (string.IsNullOrEmpty(deviceCode))
            return;

        DeviceCodeDpopBinding? binding;
        await using (var query = _sessionFactory.OpenQuerySession())
        {
            binding = await query.LoadAsync<DeviceCodeDpopBinding>(
                DeviceCodeDpopBindingKey.For(deviceCode), context.CancellationToken);
        }

        // No row (or a lapsed one) ⇒ the device request carried no proof ⇒ an
        // ordinary, unbound RFC 8628 poll.
        if (binding is null || binding.ExpiresAt <= DateTimeOffset.UtcNow)
            return;

        var items = _httpContextAccessor.HttpContext?.Items;
        var presentedJkt =
            items is not null &&
            items.TryGetValue(DpopConstants.HttpContextJktKey, out var raw) && raw is string s && s.Length > 0
                ? s
                : null;

        if (string.IsNullOrEmpty(presentedJkt))
        {
            context.Reject(DpopConstants.InvalidProofError,
                "This device code is DPoP-bound; a DPoP proof is required to redeem it.");
            return;
        }

        if (!string.Equals(presentedJkt, binding.Jkt, StringComparison.Ordinal))
        {
            context.Reject(DpopConstants.InvalidProofError,
                "The DPoP proof key does not match the key this device code is bound to.");
        }
    }
}

/// <summary>
/// Captures the binding at the device-authorization endpoint: when the request
/// carried a valid DPoP proof (validated + stashed by
/// <see cref="DpopProofValidationHandler"/>), the minted <c>device_code</c> from
/// the outgoing response is recorded against the proof key's thumbprint in a
/// <see cref="DeviceCodeDpopBinding"/> row scoped to the code's own lifetime.
/// Runs at response-apply time because that is the first (and only) point where
/// the final device_code value is observable to app code.
/// </summary>
public sealed class DpopDeviceCodeBindingCaptureHandler
    : IOpenIddictServerHandler<ApplyDeviceAuthorizationResponseContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor
            .CreateBuilder<ApplyDeviceAuthorizationResponseContext>()
            .UseScopedHandler<DpopDeviceCodeBindingCaptureHandler>()
            // MUST run before the ASP.NET host's response writer
            // (ProcessJsonResponse), which calls HandleRequest() and terminates
            // the event chain — a late order here means never running at all.
            .SetOrder(100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private readonly ITenantSessionFactory _sessionFactory;

    public DpopDeviceCodeBindingCaptureHandler(ITenantSessionFactory sessionFactory) =>
        _sessionFactory = sessionFactory;

    public async ValueTask HandleAsync(ApplyDeviceAuthorizationResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var deviceCode = context.Response?.DeviceCode;
        if (string.IsNullOrEmpty(deviceCode))
            return; // error response — nothing was minted

        var items = context.Transaction.GetHttpRequest()?.HttpContext.Items;
        if (items is null ||
            !items.TryGetValue(DpopConstants.HttpContextJktKey, out var raw) ||
            raw is not string jkt || jkt.Length == 0)
        {
            return; // no proof → an ordinary, unbound device code
        }

        var expiresIn = context.Response!.ExpiresIn is long seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(10); // OpenIddict device-code default

        await using var session = _sessionFactory.OpenSession();
        session.Store(new DeviceCodeDpopBinding
        {
            Id = DeviceCodeDpopBindingKey.For(deviceCode),
            Jkt = jkt,
            ExpiresAt = DateTimeOffset.UtcNow + expiresIn,
        });
        await session.SaveChangesAsync(context.CancellationToken);
    }
}

/// <summary>SHA-256 (uppercase hex) of the device code — the ledger key, so the
/// plaintext code never touches the database.</summary>
internal static class DeviceCodeDpopBindingKey
{
    public static string For(string deviceCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceCode)));
}
