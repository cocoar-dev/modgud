using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cocoar.Auth.Api.Hubs;

/// <summary>
/// SignalARRR hub for real-time admin notifications.
/// Pushes entity change events to connected admin clients, scoped by realm.
/// </summary>
[Authorize(Roles = "Admin")]
public class AdminHub : HARRR
{
	public AdminHub(IServiceProvider sp) : base(sp) { }

	public override async Task OnConnectedAsync()
	{
		var realmSlug = Context.GetHttpContext()?.Items["RealmSlug"] as string ?? "system";
		await Groups.AddToGroupAsync(Context.ConnectionId, $"realm:{realmSlug}");
		await base.OnConnectedAsync();
	}
}
