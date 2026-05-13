namespace Cocoar.Auth.Infrastructure.Persistence.DataProtection;

/// <summary>
/// Marten document holding a single ASP.NET Core DataProtection key as XML.
/// Stored in the system tenant (= master DB) so every instance reads from
/// the same source and a Pod-Restart doesn't invalidate the live cookies.
///
/// <para>The XML payload itself is what
/// <c>Microsoft.AspNetCore.DataProtection</c> serializes via its
/// <c>IXmlRepository</c> contract — opaque to us, validated by the
/// framework on read.</para>
///
/// <para>Encryption-at-rest: keys are NOT additionally encrypted here
/// today; the DB itself is the security boundary. Adding
/// <c>ProtectKeysWithCertificate</c> using the OpenIddict signing cert
/// is the natural Step 2 — captured in <c>ha-multi-instance.md</c> as
/// a 2a-followup once we surface that cert from Infrastructure.</para>
/// </summary>
public sealed class DataProtectionKeyDocument
{
    /// <summary>
    /// DataProtection-assigned friendly-name (UUID-shaped); supplied by
    /// the framework and stable for the lifetime of the key.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The XML element serialized by DataProtection.</summary>
    public string Xml { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
