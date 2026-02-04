using Cocoar.Auth.Infrastructure.Interfaces;
using Cocoar.Configuration.Secrets.SecretTypes;

namespace Cocoar.Auth.Api.Configuration;

/// <summary>
/// Database configuration settings.
/// </summary>
public class DatabaseSettings: IDatabaseSettings
{
    /// <summary>
    /// PostgreSQL connection string.
    /// </summary>
    public required string ConnectionString { get; set; }

    public required ISecret<string> Password { get; set; }
}
