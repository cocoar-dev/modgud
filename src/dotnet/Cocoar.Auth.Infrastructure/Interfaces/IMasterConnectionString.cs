namespace Cocoar.Auth.Infrastructure.Interfaces;

/// <summary>
/// Provides the connection string to the master (tenant registry) database.
/// </summary>
public interface IMasterConnectionString
{
	string Value { get; }
}
