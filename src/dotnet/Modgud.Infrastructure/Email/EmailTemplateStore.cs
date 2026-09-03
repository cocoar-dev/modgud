using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Net;

namespace Modgud.Infrastructure.Email;

/// <summary>
/// Stores email templates with subjects and HTML bodies.
/// Templates use Mustache-style {{Variable}} placeholders, matching Postmark's syntax.
/// Later this can be replaced with templates from ConfigHub or a database.
/// </summary>
public partial class EmailTemplateStore
{
    private static readonly Dictionary<EmailTemplate, (string Subject, string HtmlBody)> Templates = new()
    {
        [EmailTemplate.EmailOtp] = (
            Subject: "{{AppName}} — Anmelde-Code",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">Anmelde-Code</h2>
                    <p>Hallo {{DisplayName}},</p>
                    <p>Ihr Anmelde-Code lautet:</p>
                    <p style="margin: 30px 0; text-align: center;">
                        <span style="font-size: 32px; font-weight: bold; letter-spacing: 8px; background-color: #f5f5f5; padding: 16px 32px; border-radius: 8px; display: inline-block;">
                            {{Code}}
                        </span>
                    </p>
                    <p style="color: #666; font-size: 14px;">
                        Dieser Code ist {{ExpirationMinutes}} Minuten gültig.
                    </p>
                    <p style="color: #888; font-size: 0.85em;">
                        Falls Sie diese Anmeldung nicht angefordert haben, ignorieren Sie diese E-Mail.
                    </p>
                </body></html>
                """
        ),

        [EmailTemplate.MagicLink] = (
            Subject: "{{AppName}} — Anmelde-Link",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">Anmelde-Link</h2>
                    <p>Hallo {{DisplayName}},</p>
                    <p>Klicken Sie auf den folgenden Link, um sich anzumelden:</p>
                    <table cellpadding="0" cellspacing="0" border="0" role="presentation" style="margin: 30px 0;">
                        <tr>
                            <td align="center" bgcolor="#525e76" style="border-radius: 6px;">
                                <a href="{{ActionUrl}}" style="display:inline-block;padding:12px 24px;color:#ffffff;text-decoration:none;font-weight:bold;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
                                    Jetzt anmelden
                                </a>
                            </td>
                        </tr>
                    </table>
                    <p style="color: #666; font-size: 14px;">
                        Dieser Link ist {{ExpirationMinutes}} Minuten gültig und kann nur einmal verwendet werden.
                    </p>
                    <p style="color: #888; font-size: 0.85em;">
                        Falls Sie diese Anmeldung nicht angefordert haben, ignorieren Sie diese E-Mail.
                    </p>
                </body></html>
                """
        ),

        [EmailTemplate.PasswordReset] = (
            Subject: "{{AppName}} — Passwort zurücksetzen",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">Passwort zurücksetzen</h2>
                    <p>Hallo {{DisplayName}},</p>
                    <p>Sie haben eine Passwort-Zurücksetzung angefordert.</p>
                    <table cellpadding="0" cellspacing="0" border="0" role="presentation" style="margin: 30px 0;">
                        <tr>
                            <td align="center" bgcolor="#525e76" style="border-radius: 6px;">
                                <a href="{{ActionUrl}}" style="display:inline-block;padding:12px 24px;color:#ffffff;text-decoration:none;font-weight:bold;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
                                    Neues Passwort setzen
                                </a>
                            </td>
                        </tr>
                    </table>
                    <p style="color: #888; font-size: 0.85em;">
                        Dieser Link ist {{ExpirationMinutes}} Minuten gültig.
                        Falls Sie keine Zurücksetzung angefordert haben, ignorieren Sie diese E-Mail.
                    </p>
                </body></html>
                """
        ),

        [EmailTemplate.EmailVerification] = (
            Subject: "{{AppName}} — E-Mail-Adresse bestätigen",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">E-Mail-Adresse bestätigen</h2>
                    <p>Hallo {{DisplayName}},</p>
                    <p>Sie haben angefragt, diese E-Mail-Adresse für Ihr {{AppName}}-Konto zu hinterlegen. Bitte bestätigen Sie, dass Sie Zugriff auf dieses Postfach haben:</p>
                    <table cellpadding="0" cellspacing="0" border="0" role="presentation" style="margin: 30px 0;">
                        <tr>
                            <td align="center" bgcolor="#525e76" style="border-radius: 6px;">
                                <a href="{{ActionUrl}}" style="display:inline-block;padding:12px 24px;color:#ffffff;text-decoration:none;font-weight:bold;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
                                    E-Mail-Adresse bestätigen
                                </a>
                            </td>
                        </tr>
                    </table>
                    <p style="color: #666; font-size: 14px;">
                        Der Link ist {{ExpirationHours}} Stunden gültig. Nach der Bestätigung prüft ein Administrator die Änderung, bevor die neue Adresse übernommen wird.
                    </p>
                    <p style="color: #888; font-size: 0.85em;">
                        Falls Sie diese Anfrage nicht gestellt haben, ignorieren Sie diese E-Mail.
                    </p>
                </body></html>
                """
        ),

        [EmailTemplate.AdminChangeRequestNotification] = (
            Subject: "{{AppName}} — Neue Änderungsanfrage: {{Field}}",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">Neue Änderungsanfrage</h2>
                    <p><strong>{{RequestingUser}}</strong> hat eine Änderung angefragt und den Eigentümer-Nachweis erbracht. Bitte prüfen und freigeben oder ablehnen:</p>
                    <table style="margin: 20px 0; border-collapse: collapse;">
                        <tr><td style="padding: 6px 12px; color:#666;">Feld</td><td style="padding: 6px 12px; font-weight: bold;">{{Field}}</td></tr>
                        <tr><td style="padding: 6px 12px; color:#666;">Alter Wert</td><td style="padding: 6px 12px;">{{OldValue}}</td></tr>
                        <tr><td style="padding: 6px 12px; color:#666;">Neuer Wert</td><td style="padding: 6px 12px; font-weight: bold;">{{NewValue}}</td></tr>
                    </table>
                    <table cellpadding="0" cellspacing="0" border="0" role="presentation" style="margin: 30px 0;">
                        <tr>
                            <td align="center" bgcolor="#525e76" style="border-radius: 6px;">
                                <a href="{{ActionUrl}}" style="display:inline-block;padding:12px 24px;color:#ffffff;text-decoration:none;font-weight:bold;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
                                    Anfrage prüfen
                                </a>
                            </td>
                        </tr>
                    </table>
                </body></html>
                """
        ),

        [EmailTemplate.ChangeRequestApproved] = (
            Subject: "{{AppName}} — Änderung genehmigt: {{Field}}",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">Änderung genehmigt</h2>
                    <p>Hallo {{DisplayName}},</p>
                    <p>Ihre angefragte Änderung wurde genehmigt:</p>
                    <table style="margin: 20px 0; border-collapse: collapse;">
                        <tr><td style="padding: 6px 12px; color:#666;">Feld</td><td style="padding: 6px 12px; font-weight: bold;">{{Field}}</td></tr>
                        <tr><td style="padding: 6px 12px; color:#666;">Neuer Wert</td><td style="padding: 6px 12px;">{{NewValue}}</td></tr>
                    </table>
                    <p>Der neue Wert ist jetzt aktiv.</p>
                </body></html>
                """
        ),

        [EmailTemplate.RealmAdminBootstrap] = (
            Subject: "{{AppName}} — Admin-Zugang einrichten ({{RealmDisplayName}})",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">Admin-Zugang einrichten</h2>
                    <p>Hallo {{DisplayName}},</p>
                    <p>für den Realm <strong>{{RealmDisplayName}}</strong> wurde dein Admin-Zugang vorbereitet. Klick auf den folgenden Link, um dein Passwort zu setzen und dich anzumelden:</p>
                    <table cellpadding="0" cellspacing="0" border="0" role="presentation" style="margin: 30px 0;">
                        <tr>
                            <td align="center" bgcolor="#525e76" style="border-radius: 6px;">
                                <a href="{{ActionUrl}}" style="display:inline-block;padding:12px 24px;color:#ffffff;text-decoration:none;font-weight:bold;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
                                    Passwort setzen &amp; anmelden
                                </a>
                            </td>
                        </tr>
                    </table>
                    <table style="margin: 20px 0; border-collapse: collapse;">
                        <tr><td style="padding: 6px 12px; color:#666;">Benutzername</td><td style="padding: 6px 12px; font-weight: bold;">{{UserName}}</td></tr>
                        <tr><td style="padding: 6px 12px; color:#666;">E-Mail</td><td style="padding: 6px 12px;">{{Email}}</td></tr>
                    </table>
                    <p style="color: #666; font-size: 14px;">
                        Dieser Link ist {{ExpirationHours}} Stunden gültig und kann nur einmal verwendet werden.
                    </p>
                    <p style="color: #888; font-size: 0.85em;">
                        Falls du diesen Realm nicht beantragt hast, ignoriere diese E-Mail. Solange der Link nicht eingelöst wird, bleibt der Realm leer.
                    </p>
                </body></html>
                """
        ),

        [EmailTemplate.ChangeRequestRejected] = (
            Subject: "{{AppName}} — Änderung abgelehnt: {{Field}}",
            HtmlBody: """
                <!DOCTYPE html>
                <html><body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
                    <h2 style="color: #333;">Änderung abgelehnt</h2>
                    <p>Hallo {{DisplayName}},</p>
                    <p>Ihre angefragte Änderung wurde abgelehnt:</p>
                    <table style="margin: 20px 0; border-collapse: collapse;">
                        <tr><td style="padding: 6px 12px; color:#666;">Feld</td><td style="padding: 6px 12px; font-weight: bold;">{{Field}}</td></tr>
                        <tr><td style="padding: 6px 12px; color:#666;">Abgelehnter Wert</td><td style="padding: 6px 12px;">{{NewValue}}</td></tr>
                    </table>
                    <p><strong>Begründung:</strong> {{ReviewerNote}}</p>
                    <p>Bei Fragen wenden Sie sich an Ihren Administrator.</p>
                </body></html>
                """
        ),
    };

    private static readonly Dictionary<EmailTemplate, (string Subject, string HtmlBody)> EnglishTemplates = new()
    {
        [EmailTemplate.EmailOtp] = English(
            "{{AppName}} — Sign-in code", "Sign-in code",
            "<p>Hello {{DisplayName}},</p><p>Your sign-in code is:</p><p style=\"margin:30px 0;text-align:center\"><span style=\"font-size:32px;font-weight:bold;letter-spacing:8px;background:#f5f5f5;padding:16px 32px;border-radius:8px;display:inline-block\">{{Code}}</span></p><p>This code is valid for {{ExpirationMinutes}} minutes.</p><p style=\"color:#888;font-size:14px\">If you did not request this sign-in, you can ignore this email.</p>"),
        [EmailTemplate.MagicLink] = English(
            "{{AppName}} — Sign-in link", "Sign-in link",
            "<p>Hello {{DisplayName}},</p><p>Use the following link to sign in:</p>{{ActionButton}}<p>This link is valid for {{ExpirationMinutes}} minutes and can only be used once.</p><p style=\"color:#888;font-size:14px\">If you did not request this sign-in, you can ignore this email.</p>",
            "Sign in now"),
        [EmailTemplate.PasswordReset] = English(
            "{{AppName}} — Reset your password", "Reset your password",
            "<p>Hello {{DisplayName}},</p><p>You requested a password reset.</p>{{ActionButton}}<p>This link is valid for {{ExpirationMinutes}} minutes. If you did not request a reset, you can ignore this email.</p>",
            "Set a new password"),
        [EmailTemplate.EmailVerification] = English(
            "{{AppName}} — Verify your email address", "Verify your email address",
            "<p>Hello {{DisplayName}},</p><p>Please confirm that you have access to this mailbox for your {{AppName}} account.</p>{{ActionButton}}<p>The link is valid for {{ExpirationHours}} hours.</p><p style=\"color:#888;font-size:14px\">If you did not make this request, you can ignore this email.</p>",
            "Verify email address"),
        [EmailTemplate.AdminChangeRequestNotification] = English(
            "{{AppName}} — New change request: {{Field}}", "New change request",
            "<p><strong>{{RequestingUser}}</strong> requested a change.</p><p><strong>Field:</strong> {{Field}}<br><strong>Previous value:</strong> {{OldValue}}<br><strong>New value:</strong> {{NewValue}}</p>{{ActionButton}}",
            "Review request"),
        [EmailTemplate.ChangeRequestApproved] = English(
            "{{AppName}} — Change approved: {{Field}}", "Change approved",
            "<p>Hello {{DisplayName}},</p><p>Your requested change was approved.</p><p><strong>Field:</strong> {{Field}}<br><strong>New value:</strong> {{NewValue}}</p>"),
        [EmailTemplate.ChangeRequestRejected] = English(
            "{{AppName}} — Change rejected: {{Field}}", "Change rejected",
            "<p>Hello {{DisplayName}},</p><p>Your requested change was rejected.</p><p><strong>Field:</strong> {{Field}}<br><strong>Rejected value:</strong> {{NewValue}}</p><p><strong>Reason:</strong> {{ReviewerNote}}</p>"),
        [EmailTemplate.RealmAdminBootstrap] = English(
            "{{AppName}} — Set up admin access ({{RealmDisplayName}})", "Set up admin access",
            "<p>Hello {{DisplayName}},</p><p>Your admin access for <strong>{{RealmDisplayName}}</strong> is ready.</p>{{ActionButton}}<p><strong>Username:</strong> {{UserName}}<br><strong>Email:</strong> {{Email}}</p><p>This link is valid for {{ExpirationHours}} hours and can only be used once.</p>",
            "Set password and sign in"),
    };

    /// <summary>
    /// Renders a template by replacing {{Variable}} placeholders with values from the model.
    /// Returns the rendered subject and HTML body.
    /// </summary>
    public static (string Subject, string HtmlBody) Render(EmailTemplate template, Dictionary<string, string> model)
    {
        var source = string.Equals(model.GetValueOrDefault("Language"), "en", StringComparison.OrdinalIgnoreCase)
            ? EnglishTemplates
            : Templates;
        if (!source.TryGetValue(template, out var tmpl))
            throw new ArgumentException($"Unknown email template: {template}");

        var subject = ReplaceSubjectPlaceholders(tmpl.Subject, model);
        if (model.TryGetValue("SubjectPrefix", out var subjectPrefix) && !string.IsNullOrWhiteSpace(subjectPrefix))
        {
            var separator = subject.IndexOf(" — ", StringComparison.Ordinal);
            if (separator >= 0)
                subject = subjectPrefix.Replace("\r", " ").Replace("\n", " ") + subject[separator..];
        }
        return (
            Subject: subject,
            HtmlBody: ApplyBrandLayout(ReplaceHtmlPlaceholders(tmpl.HtmlBody, model), model)
        );
    }

    private static (string Subject, string HtmlBody) English(
        string subject, string heading, string content, string? actionLabel = null)
    {
        var action = actionLabel is null
            ? string.Empty
            : $"<table cellpadding=\"0\" cellspacing=\"0\" border=\"0\" role=\"presentation\" style=\"margin:30px 0\"><tr><td align=\"center\" bgcolor=\"#525e76\" style=\"border-radius:6px\"><a href=\"{{{{ActionUrl}}}}\" style=\"display:inline-block;padding:12px 24px;color:#fff;text-decoration:none;font-weight:bold\">{actionLabel}</a></td></tr></table>";
        content = content.Replace("{{ActionButton}}", action, StringComparison.Ordinal);
        return (subject,
            $"<!DOCTYPE html><html><body style=\"font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;max-width:600px;margin:0 auto;padding:20px\"><h2 style=\"color:#333\">{heading}</h2>{content}</body></html>");
    }

    /// <summary>Renders the multipart message used by SMTP/Postmark. Model values are
    /// HTML-escaped, the subject is stripped of CR/LF, and a plain-text alternative is
    /// always generated for accessibility and conservative mail clients.</summary>
    public static RenderedEmail RenderMessage(EmailTemplate template, Dictionary<string, string> model)
    {
        var (subject, html) = Render(template, model);
        return new RenderedEmail(
            subject,
            html,
            ToPlainText(html),
            model.GetValueOrDefault("FromName"),
            model.GetValueOrDefault("ReplyTo"),
            model.GetValueOrDefault("FromAddress"));
    }

    private static string ReplaceSubjectPlaceholders(string text, Dictionary<string, string> model)
    {
        return PlaceholderRegex().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            return model.TryGetValue(key, out var value)
                ? value.Replace("\r", " ").Replace("\n", " ")
                : match.Value;
        });
    }

    private static string ReplaceHtmlPlaceholders(string text, Dictionary<string, string> model) =>
        PlaceholderRegex().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            return model.TryGetValue(key, out var value)
                ? HtmlEncoder.Default.Encode(value)
                : match.Value;
        });

    private static string ApplyBrandLayout(string html, Dictionary<string, string> model)
    {
        var color = model.TryGetValue("PrimaryColor", out var candidate) && CssHexColorRegex().IsMatch(candidate)
            ? candidate
            : "#525e76";
        html = html.Replace("#525e76", color, StringComparison.OrdinalIgnoreCase);

        var appName = HtmlEncoder.Default.Encode(model.GetValueOrDefault("AppName") ?? "Modgud");
        var logo = model.GetValueOrDefault("LogoUrl");
        var logoHtml = Uri.TryCreate(logo, UriKind.Absolute, out var logoUri)
            && (logoUri.Scheme == Uri.UriSchemeHttps || logoUri.Scheme == Uri.UriSchemeHttp)
            ? $"<img src=\"{HtmlEncoder.Default.Encode(logoUri.ToString())}\" alt=\"{appName}\" style=\"display:block;max-width:180px;max-height:64px;margin:0 auto 12px;\">"
            : string.Empty;
        // Keep the application identity visible even in templates without an
        // action button (notably OTP): every branded email now carries the
        // configured primary color in its header, not only button-based mails.
        var header = $"<div style=\"text-align:center;margin:0 0 28px;\">{logoHtml}<div style=\"font-size:22px;font-weight:700;color:{color};\">{appName}</div></div>";
        if (model.TryGetValue("Preheader", out var preheader) && !string.IsNullOrWhiteSpace(preheader))
            header = $"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0\">{HtmlEncoder.Default.Encode(preheader)}</div>" + header;
        var bodyEnd = html.IndexOf('>', html.IndexOf("<body", StringComparison.OrdinalIgnoreCase));
        html = bodyEnd >= 0 ? html.Insert(bodyEnd + 1, header) : header + html;
        if (model.TryGetValue("FooterText", out var footer) && !string.IsNullOrWhiteSpace(footer))
        {
            var footerHtml = $"<div style=\"margin-top:32px;padding-top:16px;border-top:1px solid #eee;color:#777;font-size:12px\">{HtmlEncoder.Default.Encode(footer)}</div>";
            var closeBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = closeBody >= 0 ? html.Insert(closeBody, footerHtml) : html + footerHtml;
        }
        return html;
    }

    public static string ToPlainText(string html)
    {
        var text = BlockEndRegex().Replace(html, "\n");
        text = BreakRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = ExcessBlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$")]
    private static partial Regex CssHexColorRegex();

    [GeneratedRegex(@"</(p|div|h[1-6]|tr|table|li)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n\s*\n(?:\s*\n)+")]
    private static partial Regex ExcessBlankLinesRegex();
}

public sealed record RenderedEmail(
    string Subject,
    string HtmlBody,
    string TextBody,
    string? FromName = null,
    string? ReplyTo = null,
    string? FromAddress = null);
