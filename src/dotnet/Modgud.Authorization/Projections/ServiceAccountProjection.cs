using Marten.Events.Aggregation;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Projections;

/// <summary>
/// Builds service-account documents inline from their streams. Teardown stays
/// disabled because all principal subtypes share the same physical table and
/// legacy service accounts may not have a stream yet.
/// </summary>
public partial class ServiceAccountProjection : SingleStreamProjection<ServiceAccount, Guid>
{
    public ServiceAccountProjection()
    {
        Options.TeardownDataOnRebuild = false;
        IncludeType<ServiceAccountCreatedEvent>();
        IncludeType<ServiceAccountUpdatedEvent>();
        IncludeType<ServiceAccountDeletedEvent>();
    }

    public ServiceAccount Apply(ServiceAccountCreatedEvent @event, ServiceAccount _) => new()
    {
        Id = @event.Id,
        AccountName = @event.AccountName,
        Purpose = @event.Purpose,
        IsActive = @event.IsActive,
        IsDeleted = false,
    };

    public ServiceAccount Apply(ServiceAccountUpdatedEvent @event, ServiceAccount account)
    {
        account.AccountName = @event.AccountName;
        account.Purpose = @event.Purpose;
        account.IsActive = @event.IsActive;
        return account;
    }

    public ServiceAccount Apply(ServiceAccountDeletedEvent _, ServiceAccount account)
    {
        account.IsDeleted = true;
        account.IsActive = false;
        return account;
    }
}
