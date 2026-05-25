using System.Reactive.Linq;
using BuildingBlocks.EventDispatcher;
using Cocoar.SignalARRR.Common.Attributes;
using Cocoar.SignalARRR.Server;

namespace Modgud.Api.Features.ServiceAccounts;

/// <summary>
/// Per-entity SignalR pipe for the admin Service-Accounts grid. The endpoint
/// layer pushes Created/Updated/Deleted notifications through
/// <see cref="DataEventDispatcher"/> with subject "ServiceAccount"; this hub
/// just forwards them to the connected admin SPA clients.
/// </summary>
[MessageName("ServiceAccountActions")]
public class ServiceAccountHub(DataEventDispatcher eventDispatcher)
    : ServerMethods<UIHub>
{
    public IObservable<DataEvent> Subscribe()
        => eventDispatcher.Notifications.Where(ev => ev.Subject == "ServiceAccount");
}
