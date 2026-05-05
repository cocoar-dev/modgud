using System.Text.RegularExpressions;

namespace Cocoar.Auth.Infrastructure.Email;

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
                        Dieser Link ist {{ExpirationDays}} Tage gültig und kann nur einmal verwendet werden.
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

    /// <summary>
    /// Renders a template by replacing {{Variable}} placeholders with values from the model.
    /// Returns the rendered subject and HTML body.
    /// </summary>
    public static (string Subject, string HtmlBody) Render(EmailTemplate template, Dictionary<string, string> model)
    {
        if (!Templates.TryGetValue(template, out var tmpl))
            throw new ArgumentException($"Unknown email template: {template}");

        return (
            Subject: ReplacePlaceholders(tmpl.Subject, model),
            HtmlBody: ReplacePlaceholders(tmpl.HtmlBody, model)
        );
    }

    private static string ReplacePlaceholders(string text, Dictionary<string, string> model)
    {
        return PlaceholderRegex().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            return model.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
