using Cocoar.Configuration.Secrets.SecretTypes;

namespace Cocoar.Auth.Infrastructure.Interfaces;

/// <summary>
/// Database configuration settings.
/// </summary>
public interface IDatabaseSettings {
	/// <summary>
	/// PostgreSQL connection string.
	/// </summary>
	public string ConnectionString { get; }

	public ISecret<string> Password { get; }
}
