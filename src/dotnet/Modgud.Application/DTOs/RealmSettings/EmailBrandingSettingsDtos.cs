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

/// <summary>Tri-state patch: null leaves a field unchanged; empty clears it;
/// any other value replaces it.</summary>
public record UpdateEmailBrandingSettingsDto
{
    public string? ProductName { get; init; }
    public string? SubjectPrefix { get; init; }
    public string? Preheader { get; init; }
    public string? FooterText { get; init; }
    public string? FromName { get; init; }
    public string? ReplyTo { get; init; }
}
