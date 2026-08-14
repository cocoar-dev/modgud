namespace Modgud.Domain.OAuth.Storage;

/// <summary>
/// Records the DPoP key thumbprint a device code was requested with (MG-FT
/// spike — RFC 9449 applied to the RFC 8628 device flow), so the token poll can
/// require a proof of the SAME key. A short-lived companion document rather than
/// a claim inside the device code: OpenIddict builds the initial device-code
/// payload internally (not from the sign-in principal) and regenerates it at the
/// end-user approval, so nothing stamped into the token itself survives — this
/// ledger is independent of both. Stored per-realm via the tenant-scoped
/// session. <see cref="Id"/> is the SHA-256 (hex) of the device code — the
/// plaintext code never touches the database. Rows die with the device code
/// (<see cref="ExpiresAt"/>).
/// </summary>
public sealed class DeviceCodeDpopBinding
{
    /// <summary>SHA-256 (uppercase hex) of the device_code — the natural key.</summary>
    public string Id { get; set; } = default!;

    /// <summary>The bound DPoP key's JWK thumbprint (RFC 7638).</summary>
    public string Jkt { get; set; } = default!;

    /// <summary>When the underlying device code expires (and this row with it).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
