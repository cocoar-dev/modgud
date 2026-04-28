namespace Cocoar.Auth.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Provides the connection string to the master (tenant registry) database.
/// </summary>
public interface IMasterConnectionString
{
    string Value { get; }
}

internal sealed class MasterConnectionString : IMasterConnectionString
{
    public MasterConnectionString(string value) => Value = value;
    public string Value { get; }
}
