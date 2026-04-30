using System.ComponentModel;
using Serilog.Events;
using Cocoar.Auth.Authentication;
using Cocoar.Auth.Authentication.Identity;

namespace Cocoar.Auth.Api;
public class StartUpConfiguration : IServerConfiguration
{
    /// <summary>
    /// URL Kestrel binds to via <c>app.Run(AppUrl)</c>. Defaults to plain
    /// HTTP on port 80 — that's the prod-shipping shape (HTTPS terminates
    /// at the reverse proxy in front of the container, and the container
    /// itself ships no certificate). Operators who run Kestrel-direct
    /// HTTPS override this with an HTTPS URL plus <see cref="CertPath"/>
    /// + <see cref="CertPassword"/>.
    /// </summary>
    public string AppUrl { get; set; } = "http://0.0.0.0:80";

    /// <summary>
    /// Public-facing URL (used in email links, FIDO2 origins).
    /// Falls back to AppUrl if not set.
    /// </summary>
    public string? PublicUrl { get; set; }

    public string? CertPath { get; set; } = null;

    public string? CertPassword { get; set; } = null;
    
    public Logging Logging { get; set; } = new();

    public DatabaseConfiguration DbSettings { get; set;  } = new();

    public EmailConfiguration Email { get; set; } = new();

    // MagicLinkConfiguration and EmailOtpConfiguration are registered
    // as separate Cocoar.Configuration types with their own rules
}

public class Logging
{
    public string LogPath { get; set; } = "";

    private Dictionary<string, LogEventLevel?>? _loglevel;

    public Dictionary<string, LogEventLevel?> LogLevel
    {
        get => _loglevel ??= GetDefaultLoggings();
        set => _loglevel = value;
    }



    internal static Dictionary<string, LogEventLevel?> GetDefaultLoggings()
    {
        return new Dictionary<string, LogEventLevel?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Default"] = LogEventLevel.Warning,
            ["Microsoft.Hosting.Lifetime"] = LogEventLevel.Information,
        };
    }
}

public class MagicLinkConfiguration : IMagicLinkConfiguration
{
    public bool Enabled { get; set; } = true;
    public int ExpirationMinutes { get; set; } = 15;
    public int RateLimitMinutes { get; set; } = 2;
}

// EmailOtpConfiguration lives in Cocoar.Auth.Infrastructure.Identity
// (needed by EmailOtpService which is in the Infrastructure project)

public enum EmailProvider
{
    Smtp,
    Postmark,
}

public class EmailConfiguration
{
    public EmailProvider Provider { get; set; } = EmailProvider.Smtp;
    public SmtpConfiguration Smtp { get; set; } = new();
    public PostmarkConfiguration Postmark { get; set; } = new();
}

public class SmtpConfiguration
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 2525;
    public bool UseSsl { get; set; } = false;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "noreply@timetodo.local";
    public string FromName { get; set; } = "Cocoar.Auth";
}

public class PostmarkConfiguration
{
    public string ServerToken { get; set; } = "";
    public string FromAddress { get; set; } = "noreply@timetodo.local";
    public string FromName { get; set; } = "Cocoar.Auth";
    public string MessageStream { get; set; } = "outbound";
}

public class DatabaseConfiguration
{
    /// <summary>
    /// Marten connection string (PostgreSQL 18)
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
