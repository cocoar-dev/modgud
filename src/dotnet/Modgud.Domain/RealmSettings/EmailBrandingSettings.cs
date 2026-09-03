namespace Modgud.Domain.RealmSettings;

/// <summary>Realm defaults for transactional-email presentation. Visual logo
/// and colour continue to come from BrandingSettings so web and email stay in
/// one identity system.</summary>
public record EmailBrandingSettings
{
    public string? ProductName { get; init; }
    public string? SubjectPrefix { get; init; }
    public string? Preheader { get; init; }
    public string? FooterText { get; init; }
    public string? FromName { get; init; }

    /// <summary>The address outbound mail is sent FROM. Null = the deployment's
    /// configured sender (<c>Email:Smtp:FromAddress</c> / Postmark). Deliverability
    /// (SPF/DKIM/DMARC, Postmark sender signature) for a custom address is the
    /// configuring admin's responsibility; the platform does not gate it.</summary>
    public string? FromAddress { get; init; }
    public string? ReplyTo { get; init; }
}
