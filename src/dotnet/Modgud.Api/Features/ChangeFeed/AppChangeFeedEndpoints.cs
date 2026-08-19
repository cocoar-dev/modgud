using System.Text.Json;
using BuildingBlocks.Helper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Modgud.Api.Features.Management;
using Modgud.Application.DTOs.ChangeFeed;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Modgud.Api.Features.ChangeFeed;

public static class AppChangeFeedEndpoints
{
    public static WebApplication MapAppChangeFeedEndpoints(this WebApplication app, string path)
    {
        app.MapGet($"{path}/app/{{id}}/change-feed/snapshot", async (
                ShortGuid id,
                AppChangeFeedQueryService feed,
                CancellationToken cancellationToken) =>
            {
                var result = await feed.SnapshotAsync(id.Guid, cancellationToken);
                return ToResult(result);
            })
            .WithTags("Apps")
            .WithName("V2_AppChangeFeed_Snapshot")
            .RequiresManagementPermission("app-scope:read", clientAppRouteParameter: "id");

        app.MapGet($"{path}/app/{{id}}/change-feed", async (
                ShortGuid id,
                string cursor,
                int? limit,
                AppChangeFeedQueryService feed,
                CancellationToken cancellationToken) =>
            {
                var result = await feed.ReadAsync(id.Guid, cursor, limit ?? 100, cancellationToken);
                return ToResult(result);
            })
            .WithTags("Apps")
            .WithName("V2_AppChangeFeed_Read")
            .RequiresManagementPermission("app-scope:read", clientAppRouteParameter: "id");

        // A long-running integration stream needs explicit token expiry and
        // refresh semantics; first-party admin cookies remain valid for the
        // finite snapshot and polling requests only.
        app.MapGet($"{path}/app/{{id}}/change-feed/stream", StreamAsync)
            .WithTags("Apps")
            .WithName("V2_AppChangeFeed_Stream")
            .RequiresManagementPermission(
                "app-scope:read",
                clientAppRouteParameter: "id",
                bearerOnly: true);

        return app;
    }

    private static async Task StreamAsync(
        HttpContext http,
        ShortGuid id,
        string? cursor,
        int? batchSize,
        IServiceScopeFactory scopeFactory,
        IOptions<HttpJsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        cursor = string.IsNullOrWhiteSpace(cursor)
            ? http.Request.Headers["Last-Event-ID"].FirstOrDefault()
            : cursor;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            await Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "CursorRequired",
                    detail: "Take a full snapshot and pass its cursor as ?cursor= or Last-Event-ID.",
                    extensions: new Dictionary<string, object?> { ["code"] = "CursorRequired" })
                .ExecuteAsync(http);
            return;
        }

        var currentCursor = cursor;
        var currentScopeVersion = string.Empty;
        var limit = Math.Clamp(batchSize ?? 100, 1, 500);
        var responseStarted = false;
        var nextHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(15);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ManagementBearerAuthorizationError? denied;
                AppChangeFeedQueryResult<AppChangeFeedReadDto>? read = null;
                // A streaming request outlives a normal request scope. Resolve a
                // fresh tenant-scoped Marten session for every cycle so neither
                // authorization nor feed state can be served from an identity map.
                await using (var cycle = scopeFactory.CreateAsyncScope())
                {
                    denied = await cycle.ServiceProvider
                        .GetRequiredService<ManagementBearerAuthorizationService>()
                        .AuthorizeAsync(http.User, id.Guid, "app-scope:read", cancellationToken);
                    if (denied is null)
                    {
                        read = await cycle.ServiceProvider
                            .GetRequiredService<AppChangeFeedQueryService>()
                            .ReadAsync(id.Guid, currentCursor, limit, cancellationToken);
                    }
                }

                if (denied is not null)
                {
                    if (!responseStarted)
                    {
                        await AuthorizationProblem(denied).ExecuteAsync(http);
                    }
                    else
                    {
                        await WriteSseMessageAsync(
                            http.Response,
                            new AppChangeFeedMessageDto
                            {
                                ContractVersion = AppChangeFeedContract.Version,
                                Kind = "FeedEnded",
                                Cursor = currentCursor,
                                ScopeVersion = currentScopeVersion,
                                Reason = denied.Code,
                            },
                            jsonOptions.Value.SerializerOptions,
                            cancellationToken);
                    }
                    return;
                }

                if (!read!.IsSuccess)
                {
                    if (!responseStarted)
                    {
                        await ToResult(read).ExecuteAsync(http);
                    }
                    else
                    {
                        await WriteSseMessageAsync(
                            http.Response,
                            new AppChangeFeedMessageDto
                            {
                                ContractVersion = AppChangeFeedContract.Version,
                                Kind = "ResetRequired",
                                Cursor = currentCursor,
                                ScopeVersion = currentScopeVersion,
                                Reason = read.Error!.Code,
                            },
                            jsonOptions.Value.SerializerOptions,
                            cancellationToken);
                    }
                    return;
                }

                if (!responseStarted)
                {
                    StartSseResponse(http);
                    await http.Response.StartAsync(cancellationToken);
                    responseStarted = true;
                }

                currentScopeVersion = read.Value!.ScopeVersion;
                foreach (var message in read.Value!.Messages)
                {
                    currentCursor = message.Cursor;
                    if (!string.IsNullOrWhiteSpace(message.ScopeVersion))
                        currentScopeVersion = message.ScopeVersion;
                    await WriteSseMessageAsync(
                        http.Response,
                        message,
                        jsonOptions.Value.SerializerOptions,
                        cancellationToken);
                    nextHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(15);
                }

                if (read.Value.FeedEnded) return;
                if (read.Value.HasMore) continue;

                if (read.Value.Messages.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    if (DateTimeOffset.UtcNow >= nextHeartbeatAt)
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                        await http.Response.Body.FlushAsync(cancellationToken);
                        nextHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(15);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal SSE disconnect or server shutdown.
        }
    }

    private static void StartSseResponse(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static async Task WriteSseMessageAsync(
        HttpResponse response,
        AppChangeFeedMessageDto message,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        var eventName = message.Kind switch
        {
            "Change" => "change",
            "Checkpoint" => "checkpoint",
            "ResetRequired" => "reset-required",
            "FeedEnded" => "feed-ended",
            _ => "message",
        };
        var json = JsonSerializer.Serialize(message, jsonOptions);
        await response.WriteAsync($"id: {message.Cursor}\n", cancellationToken);
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static IResult AuthorizationProblem(ManagementBearerAuthorizationError error)
    {
        var unauthorized = error.Code is "invalid_client" or "invalid_subject"
            or "inactive_subject" or "token_expired";
        return Results.Problem(
            statusCode: unauthorized
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden,
            title: unauthorized ? "Unauthorized" : "Forbidden",
            detail: error.Detail,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    private static IResult ToResult<T>(AppChangeFeedQueryResult<T> result)
    {
        if (result.IsSuccess) return Results.Ok(result.Value);
        var error = result.Error!;
        return Results.Problem(
            statusCode: error.StatusCode,
            title: error.Code,
            detail: error.Detail,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
