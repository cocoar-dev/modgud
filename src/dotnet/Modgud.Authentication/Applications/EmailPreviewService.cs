using ErrorOr;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Infrastructure.Email;

namespace Modgud.Authentication.Applications;

/// <summary>
/// Renders one of the built-in transactional email templates exactly as it would be
/// sent — same <see cref="EmailTemplateStore"/>, same brand layout, same model keys —
/// so the admin preview shows the real message instead of an approximation.
///
/// <para>The effective branding comes from <see cref="IEmailBrandingResolver"/>
/// (realm, or realm + Application override when an <c>ApplicationId</c> is given).
/// An optional <see cref="EmailPreviewRequest.Branding"/> overlay carries the form's
/// UNSAVED values on top of that, so the preview tracks what the admin is typing.</para>
///
/// <para>Sample data: every template's placeholders are filled from a fixed,
/// clearly-fictional sample set (a code, a link to <c>#</c>, a display name) — never
/// from real users, and the action links are inert.</para>
/// </summary>
public interface IEmailPreviewService
{
    Task<ErrorOr<EmailPreviewResult>> RenderAsync(EmailPreviewRequest request, CancellationToken ct = default);
}

public sealed record EmailPreviewRequest
{
    /// <summary>Template name — one of <see cref="EmailTemplate"/>.</summary>
    public required string Template { get; init; }

    /// <summary>"de" (default) or "en".</summary>
    public string? Language { get; init; }

    /// <summary>Preview as this Application (realm + App override); null = the realm.</summary>
    public string? ApplicationId { get; init; }

    /// <summary>Unsaved form values overlaid on the effective branding.</summary>
    public EmailBrandingSettingsDto? Branding { get; init; }

    /// <summary>Unsaved realm branding values (product name, primary colour) overlaid
    /// on the effective branding — the brand layout reads these.</summary>
    public string? ProductName { get; init; }
    public string? PrimaryColor { get; init; }
    public string? LogoUrl { get; init; }
}

public sealed record EmailPreviewResult(
    string Template,
    string Subject,
    string From,
    string? ReplyTo,
    string HtmlBody,
    string TextBody);

public sealed class EmailPreviewService(
    IEmailBrandingResolver branding,
    IEmailSenderDefaults senderDefaults) : IEmailPreviewService
{
    public async Task<ErrorOr<EmailPreviewResult>> RenderAsync(EmailPreviewRequest request, CancellationToken ct = default)
    {
        if (!Enum.TryParse<EmailTemplate>(request.Template, ignoreCase: true, out var template))
            return Error.Validation("EmailPreview.UnknownTemplate",
                $"Unknown template '{request.Template}'. Known: {string.Join(", ", Enum.GetNames<EmailTemplate>())}.");

        Guid? applicationId = null;
        if (!string.IsNullOrWhiteSpace(request.ApplicationId))
        {
            if (!BuildingBlocks.Helper.ShortGuid.TryParse(request.ApplicationId, out Guid appId))
                return Error.Validation("EmailPreview.InvalidApplicationId", "ApplicationId is not a valid id.");
            applicationId = appId;
        }

        // Real branding first (same call the senders make), then the form overlay.
        var model = await branding.ApplyAsync(SampleModel(template), applicationId, ct: ct);
        var language = string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "de";
        model["Language"] = language;
        Overlay(model, request);

        var rendered = EmailTemplateStore.RenderMessage(template, model);

        var fromName = rendered.FromName ?? senderDefaults.FromName;
        var fromAddress = rendered.FromAddress ?? senderDefaults.FromAddress;
        var from = string.IsNullOrEmpty(fromName) ? fromAddress : $"{fromName} <{fromAddress}>";

        return new EmailPreviewResult(
            template.ToString(), rendered.Subject, from, rendered.ReplyTo, rendered.HtmlBody, rendered.TextBody);
    }

    /// <summary>Form values win over stored ones; an empty string means "cleared" and
    /// removes the key so the layout/senders fall back exactly as they would after save.</summary>
    private static void Overlay(Dictionary<string, string> model, EmailPreviewRequest request)
    {
        void Set(string key, string? value)
        {
            if (value is null) return;                 // not part of the overlay
            if (value.Trim().Length == 0) model.Remove(key); // cleared in the form
            else model[key] = value.Trim();
        }

        // AppName is special: a real send ALWAYS carries it (the resolver falls back
        // product name → realm branding → "Modgud"), so an empty form field means
        // "use that fallback", never "no name" — only a non-empty value overrides.
        var productName = request.Branding?.ProductName;
        if (string.IsNullOrWhiteSpace(productName)) productName = request.ProductName;
        if (!string.IsNullOrWhiteSpace(productName)) model["AppName"] = productName.Trim();
        Set("PrimaryColor", request.PrimaryColor);
        Set("LogoUrl", request.LogoUrl);
        if (request.Branding is { } b)
        {
            Set("SubjectPrefix", b.SubjectPrefix);
            Set("Preheader", b.Preheader);
            Set("FooterText", b.FooterText);
            Set("FromName", b.FromName);
            Set("FromAddress", b.FromAddress);
            Set("ReplyTo", b.ReplyTo);
        }
    }

    private static Dictionary<string, string> SampleModel(EmailTemplate template)
    {
        var m = new Dictionary<string, string>
        {
            ["DisplayName"] = "Alex",
            ["UserName"] = "alex",
            ["Email"] = "alex@example.com",
            ["Code"] = "483 921",
            ["ExpirationMinutes"] = "10",
            ["ExpirationHours"] = "24",
            ["WindowMinutes"] = "15",
            ["ActionUrl"] = "#",
            ["RealmDisplayName"] = "Example Realm",
            ["RequestingUser"] = "alex",
            ["Field"] = "Email",
            ["OldValue"] = "alex@old.example",
            ["NewValue"] = "alex@example.com",
            ["ReviewerNote"] = "Please use your work address.",
        };
        _ = template; // every template draws from the same superset
        return m;
    }
}

/// <summary>The deployment-level sender the resolved branding falls back to — the
/// same values the SMTP/Postmark senders use, exposed so the preview's "From" line
/// matches what actually goes out.</summary>
public interface IEmailSenderDefaults
{
    string FromAddress { get; }
    string FromName { get; }
}
