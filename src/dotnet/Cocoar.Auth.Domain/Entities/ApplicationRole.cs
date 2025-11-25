using System.Text.Json.Serialization;
using Cocoar.Auth.Domain.Common;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Represents a role in the identity system.
/// </summary>
public class ApplicationRole : Entity
{
    /// <summary>
    /// The name of the role.
    /// </summary>
    [JsonInclude]
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// The normalized (uppercase) name for lookups.
    /// </summary>
    [JsonInclude]
    public string NormalizedName { get; private set; } = string.Empty;

    /// <summary>
    /// A random value that changes when the role is persisted.
    /// </summary>
    [JsonInclude]
    public string? ConcurrencyStamp { get; private set; }

    /// <summary>
    /// A description of the role.
    /// </summary>
    [JsonInclude]
    public string? Description { get; private set; }

    /// <summary>
    /// Role claims.
    /// </summary>
    [JsonInclude]
    public List<RoleClaim> Claims { get; private set; } = [];

    // For Marten deserialization
    private ApplicationRole() : base() { }

    public ApplicationRole(string name, string? description = null) : base()
    {
        SetName(name);
        Description = description;
        ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    public void SetName(string name)
    {
        Name = name;
        NormalizedName = name.ToUpperInvariant();
        MarkModified();
    }

    public void SetDescription(string? description)
    {
        Description = description;
        MarkModified();
    }

    public void SetConcurrencyStamp(string? concurrencyStamp)
    {
        ConcurrencyStamp = concurrencyStamp;
    }

    public void AddClaim(string type, string value)
    {
        Claims.Add(new RoleClaim(type, value));
        MarkModified();
    }

    public void RemoveClaim(string type, string value)
    {
        var claim = Claims.FirstOrDefault(c => c.Type == type && c.Value == value);
        if (claim is not null)
        {
            Claims.Remove(claim);
            MarkModified();
        }
    }
}

/// <summary>
/// Represents a claim for a role.
/// </summary>
public record RoleClaim(string Type, string Value);
