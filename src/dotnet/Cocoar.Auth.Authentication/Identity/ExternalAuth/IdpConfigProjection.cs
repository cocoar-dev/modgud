using Marten.Events.Aggregation;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;

namespace Cocoar.Auth.Authentication.Identity.ExternalAuth;

/// <summary>
/// Inline projection building <see cref="IdpConfig"/> from its event stream.
/// Inline because login flows read this synchronously — any delay would show
/// up as "admin changed config but login still uses old values".
/// </summary>
public class IdpConfigProjection : SingleStreamProjection<IdpConfig, Guid>
{
    public IdpConfig Create(IdpConfigAddedEvent @event) => new()
    {
        Id = @event.Id,
        Flavor = @event.Flavor,
        DisplayName = @event.DisplayName,
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

    public IdpConfig Apply(IdpConfigUpdatedEvent @event, IdpConfig current)
    {
        current.DisplayName = @event.DisplayName;
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

    public IdpConfig Apply(IdpConfigSecretRotatedEvent @event, IdpConfig current)
    {
        current.ClientSecretEncrypted = @event.ClientSecretEncrypted;
        current.UpdatedAt = @event.RotatedAt;
        return current;
    }

    public IdpConfig Apply(IdpConfigEnabledEvent @event, IdpConfig current)
    {
        current.Enabled = true;
        current.UpdatedAt = @event.At;
        return current;
    }

    public IdpConfig Apply(IdpConfigDisabledEvent @event, IdpConfig current)
    {
        current.Enabled = false;
        current.UpdatedAt = @event.At;
        return current;
    }

    public IdpConfig Apply(IdpConfigDeletedEvent @event, IdpConfig current)
    {
        current.IsDeleted = true;
        current.Enabled = false;
        current.UpdatedAt = @event.At;
        return current;
    }
}
