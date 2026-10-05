using Microsoft.AspNetCore.Http;
using Modgud.Authentication.Domain;
using Serilog;

namespace Modgud.Authentication.SignIn;

/// <summary>ADR 0025 — response shapes the login page understands, shared by every web
/// sign-in path so the SPA sees one contract regardless of the first factor.</summary>
public static class SignInResponses
{
    /// <summary>The sign-in completed, but the target requires a second factor the user has
    /// not set up. Starts the grace on first occurrence. <c>GracePeriod = true</c> lets the
    /// user postpone; <c>false</c> makes the setup blocking.</summary>
    public static async Task<IResult> SecureSetupAsync(
        ISignInRequirementService requirements,
        ApplicationUser user,
        SignInTarget target,
        string firstFactor,
        CancellationToken ct)
    {
        var decision = await requirements.EvaluateAsync(
            user, target,
            new Dictionary<string, DateTimeOffset> { [firstFactor] = DateTimeOffset.UtcNow },
            SignInSurface.Web, startSetupGrace: true, ct);
        var inGrace = decision.Outcome == SignInOutcome.Satisfied;
        Log.Information("User requires secure setup. UserId={UserId} InGrace={InGrace} DueAt={DueAt}",
            user.Id, inGrace, decision.SetupDueAt);
        return Results.Ok(new
        {
            RequiresSecureSetup = true,
            GracePeriod = inGrace,
            SecureSetupDueAt = decision.SetupDueAt,
        });
    }
}
