using Microsoft.AspNetCore.SignalR;

namespace Cocoar.Auth.Api.Hubs;

public class AdminHubNotifier : IAdminHubNotifier
{
	private readonly IHubContext<AdminHub> _hubContext;
	private readonly IHttpContextAccessor _httpContextAccessor;

	public AdminHubNotifier(IHubContext<AdminHub> hubContext, IHttpContextAccessor httpContextAccessor)
	{
		_hubContext = hubContext;
		_httpContextAccessor = httpContextAccessor;
	}

	public async Task EntityChangedAsync(string entityType, string changeType, string? entityId = null)
	{
		var realmSlug = _httpContextAccessor.HttpContext?.Items["RealmSlug"] as string ?? "system";
		await _hubContext.Clients.Group($"realm:{realmSlug}")
			.SendAsync("OnEntityChanged", entityType, changeType, entityId);
	}
}
