using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Modgud.Application.DTOs.Realms;
using Modgud.Authentication.Setup;
using Modgud.Infrastructure.Installation;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Npgsql;

namespace Modgud.Api.Features.Installation;

public sealed record InstallationRealmRequest(
    string Slug,
    string DisplayName,
    string? Description,
    string[] Domains,
    string? PrimaryDomain);

public sealed record InstallationAdminRequest(
    string UserName,
    string Email,
    string? Firstname,
    string? Lastname,
    string Password);

public sealed record CompleteInstallationRequest(
    string Token,
    InstallationRealmRequest Realm,
    InstallationAdminRequest Admin);

public sealed record CompleteInstallationResponse(
    string RealmSlug,
    string PrimaryDomain,
    string LoginUrl);

public sealed class InstallationCompletionService(
    IInstallationChallengeService challenges,
    IRealmProvisioningService realms,
    IMasterConnectionString masterConnection,
    IServiceProvider services)
{
    // Stable deployment-wide lock id. PostgreSQL session advisory locks work
    // across API replicas and cover the cross-database provisioning saga.
    private const long InstallationLockId = 0x4D4F44475544;

    public async Task<ErrorOr<CompleteInstallationResponse>> CompleteAsync(
        CompleteInstallationRequest request,
        CancellationToken ct)
    {
        if (request.Realm is null || request.Admin is null)
            return Error.Validation("Installation.PayloadRequired", "Realm and admin are required.");
        if (string.IsNullOrWhiteSpace(request.Realm.Slug)
            || string.IsNullOrWhiteSpace(request.Realm.DisplayName))
        {
            return Error.Validation(
                "Installation.RealmRequired",
                "Realm slug and display name are required.");
        }
        if (request.Realm.Domains is not { Length: > 0 }
            || request.Realm.Domains.All(string.IsNullOrWhiteSpace))
            return Error.Validation("Installation.DomainRequired", "At least one realm domain is required.");
        if (string.IsNullOrWhiteSpace(request.Admin.UserName)
            || string.IsNullOrWhiteSpace(request.Admin.Email)
            || string.IsNullOrWhiteSpace(request.Admin.Password))
        {
            return Error.Validation(
                "Installation.AdminRequired",
                "Admin username, email and password are required.");
        }

        var tokenResult = await challenges.ValidateAsync(request.Token, ct);
        if (tokenResult.IsError) return tokenResult.Errors;
        var loginScheme = new Uri(tokenResult.Value.BaseUrl).Scheme;

        await using var lockConnection = new NpgsqlConnection(masterConnection.Value);
        await lockConnection.OpenAsync(ct);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_lock(@lockId)", lockConnection))
        {
            lockCommand.Parameters.AddWithValue("lockId", InstallationLockId);
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        var realmCreated = false;
        var adminCreated = false;
        try
        {
            // Revalidate under the cross-replica lock. A second request may have
            // completed while this one was waiting.
            var status = await challenges.GetStatusAsync(ct);
            if (status.IsInitialized)
                return Error.Conflict("Installation.AlreadyInitialized", "The deployment is already initialized.");
            tokenResult = await challenges.ValidateAsync(request.Token, ct);
            if (tokenResult.IsError) return tokenResult.Errors;

            var dto = new CreateRealmDto
            {
                Slug = request.Realm.Slug.Trim(),
                DisplayName = request.Realm.DisplayName.Trim(),
                Description = request.Realm.Description?.Trim(),
                Domains = request.Realm.Domains
                    .Select(d => d.Trim().ToLowerInvariant())
                    .Where(d => d.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                PrimaryDomain = request.Realm.PrimaryDomain?.Trim().ToLowerInvariant(),
                InitialAdmin = new InitialAdminDto
                {
                    UserName = request.Admin.UserName.Trim(),
                    Email = request.Admin.Email.Trim(),
                    Firstname = request.Admin.Firstname?.Trim(),
                    Lastname = request.Admin.Lastname?.Trim(),
                },
            };

            var realmResult = await realms.CreateInitialRealmAsync(dto, ct);
            if (realmResult.IsError) return realmResult.Errors;
            realmCreated = true;

            // Resolve a fresh tenant-scoped graph only after the tenant DB exists.
            using (TenantContext.Enter(dto.Slug))
            await using (var scope = services.CreateAsyncScope())
            {
                var bootstrapper = scope.ServiceProvider.GetRequiredService<IRealmAdminBootstrapper>();
                var adminResult = await bootstrapper.BootstrapDirectAsync(
                    request.Admin.UserName,
                    request.Admin.Password,
                    request.Admin.Email,
                    request.Admin.Firstname,
                    request.Admin.Lastname,
                    ct);
                if (adminResult.IsError)
                    return adminResult.Errors;
                adminCreated = true;
            }

            var activation = await realms.ActivateInitialRealmAsync(dto.Slug, ct);
            if (activation.IsError) return activation.Errors;

            var completion = await challenges.CompleteAsync(request.Token, dto.Slug, ct);
            if (completion.IsError) return completion.Errors;

            var primaryDomain = activation.Value.PrimaryDomain;
            return new CompleteInstallationResponse(
                dto.Slug,
                primaryDomain,
                $"{loginScheme}://{primaryDomain}/login");
        }
        finally
        {
            // Before the admin exists a failed attempt is safely compensatable.
            // Once credentials exist, keep the inactive realm for forensic/manual
            // recovery instead of silently deleting an operator identity.
            if (realmCreated && !adminCreated)
                await realms.RollbackProvisionedRealmAsync(request.Realm.Slug.Trim(), CancellationToken.None);

            await using var unlockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(@lockId)", lockConnection);
            unlockCommand.Parameters.AddWithValue("lockId", InstallationLockId);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

public static class InstallationEndpoints
{
    public static WebApplication MapInstallationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/install")
            .WithTags("Installation")
            .AllowAnonymous();

        group.MapGet("status", async (
            IInstallationChallengeService service,
            CancellationToken ct) =>
        {
            var status = await service.GetStatusAsync(ct);
            return Results.Ok(status);
        }).WithName("Installation_Status");

        group.MapPost("validate", async (
            [FromBody] TokenRequest request,
            IInstallationChallengeService service,
            CancellationToken ct) =>
        {
            var result = await service.ValidateAsync(request.Token, ct);
            return result.IsError
                ? Problem(result.FirstError)
                : Results.Ok(new { valid = true, expiresAt = result.Value.ExpiresAt });
        })
        .WithName("Installation_Validate")
        .RequireRateLimiting("bootstrap");

        group.MapPost("complete", async (
            [FromBody] CompleteInstallationRequest request,
            InstallationCompletionService service,
            CancellationToken ct) =>
        {
            var result = await service.CompleteAsync(request, ct);
            return result.IsError
                ? Problem(result.FirstError)
                : Results.Ok(result.Value);
        })
        .WithName("Installation_Complete")
        .RequireRateLimiting("bootstrap");

        return app;
    }

    public sealed record TokenRequest(string Token);

    private static IResult Problem(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Problem(statusCode: status, title: error.Code, detail: error.Description);
    }
}
