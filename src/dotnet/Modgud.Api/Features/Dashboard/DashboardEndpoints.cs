using System.Text.RegularExpressions;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Modgud.Authentication.Audit;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.LoginProviders;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.RealmSettings;
using Modgud.Authorization.Apps;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Authorization.Services;
using Modgud.Domain.Dashboard;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Domain.OAuth.Storage;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Audit;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Permissions;

namespace Modgud.Api.Features.Dashboard;

/// <summary>
/// Backs the dashboard: one aggregated statistics read, plus the stored widget
/// arrangement (per user, with a realm default an admin can set).
///
/// <para><b>Statistics are gated per block, not per endpoint.</b> Any signed-in
/// user may call <c>/dashboard/stats</c>; each number is computed only when the
/// caller holds the permission that guards the corresponding list surface
/// (<c>user:read</c> for the user count, <c>audit-log:read</c> for the login
/// series, …) and is <c>null</c> otherwise. The dashboard therefore never shows
/// a figure the caller could not have counted themselves.</para>
/// </summary>
public static partial class DashboardEndpoints
{
    private const int DefaultDays = 30;
    private const int MinDays = 7;
    private const int MaxDays = 90;

    public static WebApplication MapDashboardEndpoints(this WebApplication app, string path)
    {
        var group = app.MapGroup($"{path}/dashboard")
            .WithTags("Dashboard")
            .RequireAuthorization();

        group.MapGet("stats", GetStatsAsync).WithName("Dashboard_Stats");
        group.MapGet("my-apps", GetMyAppsAsync).WithName("Dashboard_MyApps");

        // ── Layout: the caller's own arrangement ─────────────────────────
        group.MapGet("layout", async (HttpContext http, IQuerySession session, CancellationToken ct) =>
        {
            var userId = http.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var user = await session.LoadAsync<DashboardLayout>(DashboardLayout.UserId(userId.Value), ct);
            var realm = await session.LoadAsync<DashboardLayout>(DashboardLayout.RealmDefaultId, ct);
            return Results.Ok(new DashboardLayoutsDto(user?.Widgets, realm?.Widgets));
        })
        .WithName("Dashboard_Layout_Get");

        group.MapPut("layout", async (
            [FromBody] SaveDashboardLayoutDto body,
            HttpContext http,
            IDocumentSession session,
            CancellationToken ct) =>
        {
            var userId = http.GetUserId();
            if (userId is null) return Results.Unauthorized();
            return await SaveAsync(session, DashboardLayout.UserId(userId.Value), body, ct);
        })
        .WithName("Dashboard_Layout_Save");

        group.MapDelete("layout", async (HttpContext http, IDocumentSession session, CancellationToken ct) =>
        {
            var userId = http.GetUserId();
            if (userId is null) return Results.Unauthorized();
            return await DeleteAsync(session, DashboardLayout.UserId(userId.Value), ct);
        })
        .WithName("Dashboard_Layout_Reset");

        // ── Layout: the realm default ────────────────────────────────────
        // A realm-wide setting, so it rides the realm-settings permission
        // rather than introducing a resource of its own.
        var admin = app.MapGroup($"{path}/admin/dashboard")
            .WithTags("Dashboard")
            .RequireAuthorization();

        admin.MapPut("default-layout", async (
            [FromBody] SaveDashboardLayoutDto body,
            IDocumentSession session,
            CancellationToken ct) =>
            await SaveAsync(session, DashboardLayout.RealmDefaultId, body, ct))
        .WithName("Dashboard_DefaultLayout_Save")
        .RequiresPermission("realm-settings:write");

        admin.MapDelete("default-layout", async (IDocumentSession session, CancellationToken ct) =>
            await DeleteAsync(session, DashboardLayout.RealmDefaultId, ct))
        .WithName("Dashboard_DefaultLayout_Reset")
        .RequiresPermission("realm-settings:write");

        return app;
    }

    // ───────────────────────────────────────────── Layout ──────────────────

    private static async Task<IResult> SaveAsync(
        IDocumentSession session, string id, SaveDashboardLayoutDto body, CancellationToken ct)
    {
        if (Validate(body) is { } problem)
            return Results.BadRequest(new { Error = "Dashboard.InvalidLayout", Message = problem });

        session.Store(new DashboardLayout
        {
            Id = id,
            Widgets = body.Widgets!,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(IDocumentSession session, string id, CancellationToken ct)
    {
        session.Delete<DashboardLayout>(id);
        await session.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Shape check only — the widget catalog is the frontend's, so an
    /// unknown id is stored as-is and ignored at render time.</summary>
    private static string? Validate(SaveDashboardLayoutDto body)
    {
        if (body.Widgets is null) return "Widgets is required.";
        if (body.Widgets.Count > DashboardLayout.MaxWidgets)
            return $"A layout holds at most {DashboardLayout.MaxWidgets} widgets.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in body.Widgets)
        {
            if (placement is null || placement.Widget is null || !WidgetIdPattern().IsMatch(placement.Widget))
                return "Widget ids are lower-case letters, digits and dashes (1-64 characters).";
            if (placement.Size is null || !DashboardWidgetPlacement.Sizes.Contains(placement.Size))
                return $"Size must be one of: {string.Join(", ", DashboardWidgetPlacement.Sizes.Order())}.";
            if (!seen.Add(placement.Widget))
                return $"Widget '{placement.Widget}' is listed twice.";
            if (ValidateOptions(placement.Options) is { } optionProblem)
                return optionProblem;
        }

        return null;
    }

    private static string? ValidateOptions(Dictionary<string, List<string>>? options)
    {
        if (options is null) return null;
        if (options.Count > DashboardWidgetPlacement.MaxOptions)
            return $"A widget holds at most {DashboardWidgetPlacement.MaxOptions} options.";

        foreach (var (key, values) in options)
        {
            if (!OptionTokenPattern().IsMatch(key))
                return "Option keys are letters, digits and dashes (1-64 characters).";
            if (values is null || values.Count > DashboardWidgetPlacement.MaxOptionValues)
                return $"An option holds at most {DashboardWidgetPlacement.MaxOptionValues} values.";
            if (values.Any(v => v is null || !OptionTokenPattern().IsMatch(v)))
                return "Option values are letters, digits and dashes (1-64 characters).";
        }

        return null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex WidgetIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,63}$")]
    private static partial Regex OptionTokenPattern();

    // ───────────────────────────────────────────── My apps ─────────────────

    private const int MyAppsScan = 500;

    /// <summary>
    /// The OAuth clients the caller has a valid authorization with — "where have
    /// I signed in with this account". Strictly the caller's own grants; one row
    /// per client however many authorizations it holds.
    /// </summary>
    private static async Task<IResult> GetMyAppsAsync(
        HttpContext http, IQuerySession session, CancellationToken ct)
    {
        var userId = http.GetUserId();
        if (userId is null) return Results.Unauthorized();

        var subject = userId.Value.ToString();
        var grants = await session.Query<OpenIddictAuthorizationDocument>()
            .Where(x => x.Subject == subject && x.Status == OpenIddict.Abstractions.OpenIddictConstants.Statuses.Valid)
            .OrderByDescending(x => x.CreationDate)
            .Take(MyAppsScan)
            .ToListAsync(ct);

        var byClient = grants
            .Where(g => Guid.TryParse(g.ApplicationId, out _))
            .GroupBy(g => Guid.Parse(g.ApplicationId!))
            .ToDictionary(g => g.Key, g => (First: g.Min(x => x.CreationDate), Last: g.Max(x => x.CreationDate)));

        var clientKeys = byClient.Keys.ToList();
        var clients = clientKeys.Count == 0
            ? []
            : await session.Query<OAuthApplicationState>()
                .Where(c => clientKeys.Contains(c.Id) && !c.IsDeleted)
                .ToListAsync(ct);

        var appIds = clients.SelectMany(c => c.AppIds).Distinct().ToList();
        var apps = appIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await session.Query<App>().Where(a => appIds.Contains(a.Id) && !a.IsDeleted).ToListAsync(ct))
                .ToDictionary(a => a.Id, a => a.DisplayName);

        var rows = clients
            .Select(c => new MyAppDto(
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.ClientId : c.DisplayName,
                c.AppIds.Select(id => apps.GetValueOrDefault(id)).FirstOrDefault(name => !string.IsNullOrEmpty(name)),
                byClient[c.Id].First,
                byClient[c.Id].Last))
            .OrderByDescending(r => r.LastAuthorizedAt)
            .ToList();

        return Results.Ok(rows);
    }

    // ───────────────────────────────────────────── Statistics ──────────────

    private static async Task<IResult> GetStatsAsync(
        HttpContext http,
        IDocumentSession session,
        IPermissionService permissions,
        IRealmSettingsService realmSettings,
        AppSettings settings,
        int? days,
        string? tz,
        CancellationToken ct)
    {
        var userId = http.GetUserId();
        if (userId is null) return Results.Unauthorized();

        var grants = await permissions.GetUserPermissionsAsync(userId.Value, AppSlugs.Modgud, ct);
        bool Can(string permission) => PermissionEvaluator.Evaluate(grants, permission);

        var now = DateTimeOffset.UtcNow;
        var window = Math.Clamp(days ?? DefaultDays, MinDays, MaxDays);
        var (zoneId, zone) = ResolveZone(tz);

        var counts = new DashboardCountsDto(
            Users: Can("user:read")
                ? await session.Query<UserView>().CountAsync(u => !u.IsDeleted, ct) : null,
            ServiceAccounts: Can("service-account:read")
                ? await session.Query<ServiceAccount>().CountAsync(s => !s.IsDeleted, ct) : null,
            Positions: settings.Features.PositionTerminals && Can("position:read")
                ? await session.Query<PositionPrincipal>().CountAsync(p => !p.IsDeleted, ct) : null,
            Groups: Can("authorization-group:read")
                ? await session.Query<Modgud.Authorization.Principals.Group>().CountAsync(g => !g.IsDeleted, ct) : null,
            Roles: Can("permission-role:read")
                ? await session.Query<PermissionRole>().CountAsync(r => !r.IsDeleted, ct) : null,
            Apps: Can("app:read")
                ? await session.Query<App>().CountAsync(a => !a.IsDeleted, ct) : null,
            OAuthClients: Can("oauth-client:read")
                ? await session.Query<OAuthApplicationState>().CountAsync(c => !c.IsDeleted, ct) : null,
            OAuthApis: Can("oauth-api:read")
                ? await session.Query<OAuthApiState>().CountAsync(a => !a.IsDeleted, ct) : null,
            OAuthScopes: Can("oauth-scope:read")
                ? await session.Query<OAuthScopeState>().CountAsync(s => !s.IsDeleted, ct) : null,
            LoginProviders: Can("login-provider:read")
                ? await session.Query<LoginProvider>().CountAsync(p => !p.IsDeleted, ct) : null,
            LoginProvidersEnabled: Can("login-provider:read")
                ? await session.Query<LoginProvider>().CountAsync(p => !p.IsDeleted && p.Enabled, ct) : null,
            ActiveSessions: Can("session:read")
                ? await session.Query<UserSession>().CountAsync(s => s.ExpiresAt > now && s.AbsoluteExpiresAt > now, ct) : null,
            PendingChangeRequests: Can("user:write")
                ? await session.Query<UserChangeRequest>().CountAsync(
                    r => r.Status == ChangeRequestStatus.AdminApprovalPending
                      || r.Status == ChangeRequestStatus.EmailVerificationPending, ct)
                : null);

        LoginStatsDto? logins = null;
        if (Can("audit-log:read"))
        {
            // Same view bound as the audit grid: never chart further back than
            // the realm lets an admin read.
            var visible = ((await realmSettings.LoadAsync(ct)).Audit ?? AuditSettings.Defaults).VisibilityWindowDays;
            logins = await LoadLoginsAsync(session, Math.Min(window, Math.Max(1, visible)), zoneId, zone, now, ct);
        }

        var security = Can("auth-log:read")
            ? await LoadSecurityAsync(session, window, zoneId, zone, now, ct)
            : null;

        return Results.Ok(new DashboardStatsDto(window, zoneId, counts, logins, security));
    }

    /// <summary>One <c>group by</c> row, shipped back as JSON so Marten's
    /// serializer materialises it like any other document.</summary>
    private sealed record Bucket(string Day, string? Key, string? Sub, int N);

    private const string LoginBucketsSql = """
        select jsonb_build_object('Day', b.day, 'Key', b.key, 'Sub', b.sub, 'N', b.n)
        from (
            select to_char(((d.data ->> 'Timestamp')::timestamptz at time zone ?)::date, 'YYYY-MM-DD') as day,
                   d.data ->> 'EventType' as key,
                   d.data ->> 'Method' as sub,
                   sum(coalesce((d.data ->> 'Count')::int, 1))::int as n
            from mt_doc_auth_audit_view d
            where d.data ->> 'Category' = ?
              and (d.data ->> 'Timestamp')::timestamptz >= ?
            group by 1, 2, 3
        ) b
        """;

    private const string SecurityBucketsSql = """
        select jsonb_build_object('Day', b.day, 'Key', b.key, 'Sub', b.sub, 'N', b.n)
        from (
            select to_char(((d.data ->> 'Timestamp')::timestamptz at time zone ?)::date, 'YYYY-MM-DD') as day,
                   d.data ->> 'Severity' as key,
                   d.data ->> 'EventType' as sub,
                   count(*)::int as n
            from mt_doc_realm_security_audit_event d
            where (d.data ->> 'Timestamp')::timestamptz >= ?
            group by 1, 2, 3
        ) b
        """;

    private static async Task<LoginStatsDto> LoadLoginsAsync(
        IDocumentSession session, int days, string zoneId, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        var (dayKeys, since) = Window(days, zone, now);

        // The raw SQL below bypasses Marten's lazy schema creation.
        await session.Database.EnsureStorageExistsAsync(typeof(AuthAuditView), ct);
        var buckets = await session.QueryAsync<Bucket>(
            LoginBucketsSql, ct, zoneId, AuditCategories.Authentication, since);

        var succeeded = dayKeys.ToDictionary(d => d, _ => 0);
        var failed = dayKeys.ToDictionary(d => d, _ => 0);
        var methods = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in buckets)
        {
            if (!succeeded.ContainsKey(b.Day)) continue;
            switch (b.Key)
            {
                case AuditEvents.LoginSucceeded:
                    succeeded[b.Day] += b.N;
                    var method = string.IsNullOrEmpty(b.Sub) ? "unknown" : b.Sub;
                    methods[method] = methods.GetValueOrDefault(method) + b.N;
                    break;
                // A failure is either a single row or a streak summary carrying
                // its count — the SQL already folded Count into N.
                case AuditEvents.LoginFailed or AuditEvents.LoginFailuresObserved:
                    failed[b.Day] += b.N;
                    break;
            }
        }

        var dayAgo = now.AddHours(-24);
        var failedLast24h = (await session.Query<AuthAuditView>()
                .Where(x => x.Timestamp >= dayAgo
                    && (x.EventType == AuditEvents.LoginFailed || x.EventType == AuditEvents.LoginFailuresObserved))
                .Select(x => x.Count)
                .ToListAsync(ct))
            .Sum(count => count ?? 1);

        return new LoginStatsDto(
            days,
            succeeded.Values.Sum(),
            failed.Values.Sum(),
            failedLast24h,
            dayKeys.Select(d => new LoginDayDto(d, succeeded[d], failed[d])).ToList(),
            methods.OrderByDescending(m => m.Value).ThenBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => new LoginMethodDto(m.Key, m.Value)).ToList());
    }

    private static async Task<SecurityStatsDto> LoadSecurityAsync(
        IDocumentSession session, int days, string zoneId, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        var (dayKeys, since) = Window(days, zone, now);

        await session.Database.EnsureStorageExistsAsync(typeof(RealmSecurityAuditEvent), ct);
        var buckets = await session.QueryAsync<Bucket>(SecurityBucketsSql, ct, zoneId, since);

        var info = dayKeys.ToDictionary(d => d, _ => 0);
        var warning = dayKeys.ToDictionary(d => d, _ => 0);
        var error = dayKeys.ToDictionary(d => d, _ => 0);
        var attentionByType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in buckets)
        {
            if (!info.ContainsKey(b.Day)) continue;
            var target = b.Key switch
            {
                nameof(AuditSeverity.Error) => error,
                nameof(AuditSeverity.Warning) => warning,
                _ => info,
            };
            target[b.Day] += b.N;

            if (!ReferenceEquals(target, info) && !string.IsNullOrEmpty(b.Sub))
                attentionByType[b.Sub] = attentionByType.GetValueOrDefault(b.Sub) + b.N;
        }

        var dayAgo = now.AddHours(-24);
        var attentionLast24h = await session.Query<RealmSecurityAuditEvent>()
            .CountAsync(x => x.Timestamp >= dayAgo && x.Severity != AuditSeverity.Info, ct);

        return new SecurityStatsDto(
            days,
            info.Values.Sum(),
            warning.Values.Sum(),
            error.Values.Sum(),
            attentionLast24h,
            dayKeys.Select(d => new SecurityDayDto(d, info[d], warning[d], error[d])).ToList(),
            attentionByType.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal)
                .Take(5).Select(e => new SecurityEventTypeDto(e.Key, e.Value)).ToList());
    }

    /// <summary>The viewer's last <paramref name="days"/> calendar days (today
    /// included) as <c>yyyy-MM-dd</c> keys, and the UTC instant the first begins.</summary>
    private static (List<string> DayKeys, DateTimeOffset Since) Window(int days, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = today.AddDays(-(days - 1));

        var midnight = first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A zone whose DST jump skips midnight has no 00:00 on that date.
        if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        var since = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero);

        var keys = Enumerable.Range(0, days)
            .Select(i => first.AddDays(i).ToString("yyyy-MM-dd"))
            .ToList();
        return (keys, since);
    }

    /// <summary>Accepts an IANA zone id (what the browser reports and what
    /// PostgreSQL's <c>AT TIME ZONE</c> understands); anything else buckets in UTC.</summary>
    private static (string Id, TimeZoneInfo Zone) ResolveZone(string? tz)
    {
        if (tz is not null && IanaZonePattern().IsMatch(tz)
            && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var zone))
            return (tz, zone);
        return ("UTC", TimeZoneInfo.Utc);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){1,2}$")]
    private static partial Regex IanaZonePattern();
}

public sealed record DashboardLayoutsDto(
    List<DashboardWidgetPlacement>? User,
    List<DashboardWidgetPlacement>? RealmDefault);

public sealed record SaveDashboardLayoutDto(List<DashboardWidgetPlacement>? Widgets);

/// <summary>A client the caller authorized. <paramref name="AppName"/> is the
/// Application the client belongs to, when it belongs to one.</summary>
public sealed record MyAppDto(
    string Name,
    string? AppName,
    DateTimeOffset? FirstAuthorizedAt,
    DateTimeOffset? LastAuthorizedAt);

/// <summary><paramref name="Days"/> is the requested window; a block may cover
/// fewer days (see <see cref="LoginStatsDto.Days"/>).</summary>
public sealed record DashboardStatsDto(
    int Days,
    string TimeZone,
    DashboardCountsDto Counts,
    LoginStatsDto? Logins,
    SecurityStatsDto? Security);

/// <summary>Each count is <c>null</c> when the caller lacks the permission that
/// guards the matching list.</summary>
public sealed record DashboardCountsDto(
    int? Users,
    int? ServiceAccounts,
    int? Positions,
    int? Groups,
    int? Roles,
    int? Apps,
    int? OAuthClients,
    int? OAuthApis,
    int? OAuthScopes,
    int? LoginProviders,
    int? LoginProvidersEnabled,
    int? ActiveSessions,
    int? PendingChangeRequests);

/// <summary>Sign-ins of known accounts, from the realm's audit trail. Attempts
/// against identifiers matching no account are security events, not logins.</summary>
public sealed record LoginStatsDto(
    int Days,
    int Succeeded,
    int Failed,
    int FailedLast24h,
    List<LoginDayDto> Series,
    List<LoginMethodDto> Methods);

public sealed record LoginDayDto(string Day, int Succeeded, int Failed);

public sealed record LoginMethodDto(string Method, int Count);

public sealed record SecurityStatsDto(
    int Days,
    int Info,
    int Warning,
    int Error,
    int AttentionLast24h,
    List<SecurityDayDto> Series,
    List<SecurityEventTypeDto> TopEventTypes);

public sealed record SecurityDayDto(string Day, int Info, int Warning, int Error);

public sealed record SecurityEventTypeDto(string EventType, int Count);
