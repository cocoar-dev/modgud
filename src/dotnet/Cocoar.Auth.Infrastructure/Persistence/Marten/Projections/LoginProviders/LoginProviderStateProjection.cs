using Cocoar.Auth.Domain.Identity.LoginProviders;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.LoginProviders;

public class LoginProviderStateProjection : SingleStreamProjection<LoginProviderState, Guid>
{
    public LoginProviderState Create(LoginProviderCreated e) => new()
    {
        Id = e.LoginProviderId,
        Name = e.Name,
        DisplayName = e.DisplayName,
        Description = e.Description,
        Type = e.Type,
        Configuration = new Dictionary<string, string>(e.Configuration),
        IsBuiltIn = e.IsBuiltIn,
    };

    public void Apply(LoginProviderNameChanged e, LoginProviderState s) => s.Name = e.NewName;
    public void Apply(LoginProviderDisplayNameChanged e, LoginProviderState s) => s.DisplayName = e.NewDisplayName;
    public void Apply(LoginProviderDescriptionChanged e, LoginProviderState s) => s.Description = e.NewDescription;
    public void Apply(LoginProviderConfigurationChanged e, LoginProviderState s) => s.Configuration = new Dictionary<string, string>(e.NewConfiguration);
    public void Apply(LoginProviderDeleted e, LoginProviderState s) => s.IsDeleted = true;
}
