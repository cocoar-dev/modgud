namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// Interface for SMTP settings.
/// </summary>
public interface ISmtpSettings
{
    string Host { get; }
    int Port { get; }
    bool UseSsl { get; }
    string? Username { get; }
    string? Password { get; }
    string FromAddress { get; }
    string FromName { get; }
}

/// <summary>
/// SMTP settings for email sending.
/// </summary>
public class SmtpSettings : ISmtpSettings
{
    /// <summary>
    /// SMTP server hostname.
    /// </summary>
    public required string Host { get; set; }

    /// <summary>
    /// SMTP server port.
    /// </summary>
    public required int Port { get; set; }

    /// <summary>
    /// Whether to use SSL/TLS.
    /// </summary>
    public bool UseSsl { get; set; }

    /// <summary>
    /// Username for SMTP authentication (optional).
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for SMTP authentication (optional).
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Email address to send from.
    /// </summary>
    public required string FromAddress { get; set; }

    /// <summary>
    /// Display name for the sender.
    /// </summary>
    public required string FromName { get; set; }
}
