using Modgud.Domain.Common;

namespace Modgud.Application.DTOs.RealmSettings;

public record EmailBrandingSettingsDto
{
    public string? ProductName { get; init; }
    public string? SubjectPrefix { get; init; }
    public string? Preheader { get; init; }
    public string? FooterText { get; init; }
    public string? FromName { get; init; }
    public string? ReplyTo { get; init; }
}

/// <summary>v2 merge-patch: absent = unchanged; explicit null (or a blank
/// string) clears back to the Branding/template fallback; other = replace.</summary>
public record UpdateEmailBrandingSettingsDto
{
    public Optional<string?> ProductName { get; init; }
    public Optional<string?> SubjectPrefix { get; init; }
    public Optional<string?> Preheader { get; init; }
    public Optional<string?> FooterText { get; init; }
    public Optional<string?> FromName { get; init; }
    public Optional<string?> ReplyTo { get; init; }
}
