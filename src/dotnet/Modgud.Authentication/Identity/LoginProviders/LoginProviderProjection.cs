using Marten.Events.Aggregation;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.Domain.LoginProviders.Events;

namespace Modgud.Authentication.Identity.LoginProviders;

/// <summary>
/// Inline projection building <see cref="LoginProvider"/> from its event stream.
/// Inline because login flows read this synchronously — any delay would show
/// up as "admin changed config but login still uses old values".
/// </summary>
public partial class LoginProviderProjection : SingleStreamProjection<LoginProvider, Guid>
{
    public LoginProvider Create(LoginProviderAddedEvent @event) => new()
    {
        Id = @event.Id,
        Type = @event.Type,
        Flavor = @event.Flavor,
        DisplayName = @event.DisplayName,
        Description = @event.Description,
        IsBuiltIn = @event.IsBuiltIn,
        Enabled = @event.Enabled,
        ClientId = @event.ClientId,
        ClientSecretEncrypted = @event.ClientSecretEncrypted,
        Scopes = @event.Scopes,
        UserUpdateScript = @event.UserUpdateScript,
        StoreRawClaims = @event.StoreRawClaims,
        RawClaimsRetentionDays = @event.RawClaimsRetentionDays,
        AutoCreateUsers = @event.AutoCreateUsers,
        AllowLinking = @event.AllowLinking,
        TrustForEmailLink = @event.TrustForEmailLink,
        AllowedEmailDomains = @event.AllowedEmailDomains,
        IconName = @event.IconName,
        ButtonColorHex = @event.ButtonColorHex,
        FlavorData = @event.FlavorData,
        CreatedAt = @event.CreatedAt,
        UpdatedAt = @event.CreatedAt,
        IsDeleted = false,
    };

    public LoginProvider Apply(LoginProviderUpdatedEvent @event, LoginProvider current)
    {
        current.DisplayName = @event.DisplayName;
        current.Description = @event.Description;
        current.ClientId = @event.ClientId;
        current.Scopes = @event.Scopes;
        current.UserUpdateScript = @event.UserUpdateScript;
        current.StoreRawClaims = @event.StoreRawClaims;
        current.RawClaimsRetentionDays = @event.RawClaimsRetentionDays;
        current.AutoCreateUsers = @event.AutoCreateUsers;
        current.AllowLinking = @event.AllowLinking;
        current.TrustForEmailLink = @event.TrustForEmailLink;
        current.AllowedEmailDomains = @event.AllowedEmailDomains;
        current.IconName = @event.IconName;
        current.ButtonColorHex = @event.ButtonColorHex;
        current.FlavorData = @event.FlavorData;
        current.UpdatedAt = @event.UpdatedAt;
        return current;
    }

    public LoginProvider Apply(LoginProviderSecretRotatedEvent @event, LoginProvider current)
    {
        current.ClientSecretEncrypted = @event.ClientSecretEncrypted;
        current.UpdatedAt = @event.RotatedAt;
        return current;
    }

    public LoginProvider Apply(LoginProviderEnabledEvent @event, LoginProvider current)
    {
        current.Enabled = true;
        current.UpdatedAt = @event.At;
        return current;
    }

    public LoginProvider Apply(LoginProviderDisabledEvent @event, LoginProvider current)
    {
        current.Enabled = false;
        current.UpdatedAt = @event.At;
        return current;
    }

    public LoginProvider Apply(LoginProviderDeletedEvent @event, LoginProvider current)
    {
        current.IsDeleted = true;
        current.Enabled = false;
        current.UpdatedAt = @event.At;
        return current;
    }
}
