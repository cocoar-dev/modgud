using BuildingBlocks.Helper;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using Modgud.Application.Scheduling;
using Modgud.Authorization.AspNetCore;

namespace Modgud.Api.Features.Admin.Jobs;

/// <summary>
/// Admin surface for scheduled jobs. Realm-job configuration and history are
/// stored in the calling realm's Marten session and address only that realm's
/// Quartz identities. Deployment-wide system jobs are additionally returned
/// only for the current Control-Plane realm. Realm-admin bypass and granular
/// <c>scheduled-job:read</c>/<c>scheduled-job:write</c> delegation apply within
/// that visibility boundary.
/// </summary>
public static class JobsEndpoints
{
    public static WebApplication MapJobsEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/admin/jobs")
            .WithTags("Admin / Scheduled Jobs")
            .RequireAuthorization();

        group.MapGet("", async (IJobsService jobs, CancellationToken ct) =>
            Results.Ok(await jobs.GetAllAsync(ct)))
            .WithName("V2_AdminJobs_GetAll")
            .RequiresPermission("scheduled-job:read");

        group.MapGet("{key}", async (string key, IJobsService jobs, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(key, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        })
            .WithName("V2_AdminJobs_GetByKey")
            .RequiresPermission("scheduled-job:read");

        group.MapGet("{key}/history", async (string key, IJobsService jobs, int take, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await jobs.GetHistoryAsync(key, take == 0 ? 50 : take, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
            .WithName("V2_AdminJobs_GetHistory")
            .RequiresPermission("scheduled-job:read");

        group.MapPut("{key}", async (string key, [FromBody] JobUpdateDto body, IJobsService jobs, CancellationToken ct) =>
        {
            // Validate the cron expression up-front so the admin gets a clear
            // 400 instead of a runtime scheduler failure later.
            if (!string.IsNullOrWhiteSpace(body.CronOverride) && !CronExpression.IsValidExpression(body.CronOverride))
                return Results.BadRequest(new { error = "Invalid Quartz cron expression" });

            try
            {
                await jobs.UpdateAsync(key, body, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
            .WithName("V2_AdminJobs_Update")
            .RequiresPermission("scheduled-job:write");

        group.MapPost("{key}/trigger", async (
            string key,
            HttpContext ctx,
            IJobsService jobs,
            CancellationToken ct) =>
        {
            try
            {
                // Capture the triggering admin so the job→inbox bridge can route
                // the ManualJobCompleted notification back to the right inbox.
                var triggeredBy = Modgud.Authentication.ExtensionMethods.HttpContextExtensions.GetUserId(ctx);
                await jobs.TriggerNowAsync(key, triggeredBy, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
            .WithName("V2_AdminJobs_TriggerNow")
            .RequiresPermission("scheduled-job:write");

        return app;
    }
}
