using Cocoar.SignalARRR.Server;
using Cocoar.SignalARRR.Server.ExtensionMethods;

namespace Cocoar.Auth.Api.Hubs;

public class AdminHubNotifier : IAdminHubNotifier
{
	private readonly ClientManager _clients;
	private readonly IHttpContextAccessor _httpContextAccessor;

	public AdminHubNotifier(ClientManager clients, IHttpContextAccessor httpContextAccessor)
	{
		_clients = clients;
		_httpContextAccessor = httpContextAccessor;
	}

	public async Task EntityChangedAsync(string entityType, string changeType, string? entityId = null)
	{
		var realmSlug = _httpContextAccessor.HttpContext?.Items["RealmSlug"] as string ?? "system";
		await _clients.WithHub<AdminHub>().WithGroup($"realm:{realmSlug}")
			.SendAsync<IAdminHubClient>(c => c.OnEntityChanged(entityType, changeType, entityId));
	}
}
