using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cocoar.Auth.Api.Filters;

/// <summary>
/// Restricts an action or controller to only be accessible from the system realm.
/// Returns 404 for requests from any other realm.
/// </summary>
public class SystemRealmOnlyAttribute : ActionFilterAttribute
{
	public override void OnActionExecuting(ActionExecutingContext context)
	{
		if (context.HttpContext.Items["TenantId"] as string != "system")
		{
			context.Result = new NotFoundResult();
		}
	}
}
