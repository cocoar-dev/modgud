namespace TimeToDo.TestIdP;

/// <summary>
/// Test-only administrative endpoints for wiring up OpenIddict clients at
/// runtime. Used by E2E harnesses where the <c>IdpConfig</c> GUID (and thus
/// the callback URI) is only known after the TimeToDo app has started and
/// the admin UI has created the config.
/// </summary>
public static class AdminEndpoints
{
    public record RegisterRedirectRequest(string ClientId, string RedirectUri);

    public static void Map(WebApplication app)
    {
        app.MapPost("/admin/register-redirect", async (RegisterRedirectRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.RedirectUri))
                return Results.BadRequest(new { error = "ClientId and RedirectUri are required." });
            if (!Uri.TryCreate(req.RedirectUri, UriKind.Absolute, out _))
                return Results.BadRequest(new { error = "RedirectUri must be absolute." });

            try
            {
                await TestIdpHost.AddRedirectUriAsync(app.Services, req.ClientId, req.RedirectUri);
                TestIdpLog.Write($"[admin] registered redirect '{req.RedirectUri}' for client '{req.ClientId}'");
                return Results.Ok(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).AllowAnonymous();
    }
}
