using Cocoar.SignalARRR.Server;
using Microsoft.AspNetCore.Authorization;

namespace Cocoar.Auth.Api;

[Authorize]
public class UIHub : HARRR {
    public UIHub(IServiceProvider serviceProvider) : base(serviceProvider) {
    }
}
