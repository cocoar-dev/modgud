using Cocoar.Auth.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cocoar.Auth.Api.Filters;

/// <summary>
/// Restricts an action or controller to tenants that have CanManageTenants enabled.
/// Returns 404 for requests from tenants without this capability.
/// </summary>
public class CanManageTenantsAttribute : ActionFilterAttribute
{
	public override void OnActionExecuting(ActionExecutingContext context)
	{
		var tenantInfo = context.HttpContext.Items["TenantInfo"] as TenantInfo;
		if (tenantInfo is null || !tenantInfo.CanManageTenants)
		{
			context.Result = new NotFoundResult();
		}
	}
}
