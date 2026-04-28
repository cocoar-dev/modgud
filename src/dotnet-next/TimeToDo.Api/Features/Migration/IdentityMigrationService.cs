using Marten;
using TimeToDo.Authentication.Domain;
using TimeToDo.Authentication.Events;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Api.Features.Migration;

/// <summary>
/// One-time migration: ensures every UserView has a UserName (defaults to Acronym).
/// Creates ApplicationUser documents for users that don't have one.
/// Stores a marker document to avoid re-running.
/// </summary>
public class IdentityMigrationService(IServiceProvider services, ILogger<IdentityMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // Check if migration already ran
        var marker = await session.LoadAsync<IdentityMigrationMarker>(IdentityMigrationMarker.MarkerId, ct);
        if (marker is not null)
        {
            logger.LogInformation("Identity migration already completed at {CompletedAt}", marker.CompletedAt);
            return;
        }

        logger.LogInformation("Starting identity migration...");

        var users = await session.Query<UserView>()
            .Where(u => !u.IsDeleted)
            .ToListAsync(ct);

        var migratedCount = 0;

        foreach (var user in users)
        {
            // Determine UserName: prefer existing UserName, then Acronym, then Id — always lowercase
            var userName = (user.UserName ?? user.Acronym ?? user.Id.ToString()).ToLowerInvariant();

            // Ensure uniqueness by appending suffix if needed
            var baseName = userName;
            var suffix = 1;
            while (await session.Query<ApplicationUser>()
                .Where(u => u.NormalizedUserName == userName.ToUpperInvariant())
                .AnyAsync(ct))
            {
                userName = $"{baseName}_{suffix++}";
            }

            // Check if ApplicationUser already exists for this user
            var existingAppUser = await session.LoadAsync<ApplicationUser>(user.Id, ct);
            if (existingAppUser is null)
            {
                // Create ApplicationUser document (no password)
                // Normalize empty email to null (avoids unique index issues)
                var email = string.IsNullOrWhiteSpace(user.Email) ? null : user.Email;
                var appUser = new ApplicationUser(userName, email)
                {
                    Id = user.Id,
                    Firstname = user.Firstname,
                    Lastname = user.Lastname,
                    Acronym = user.Acronym,
                    IsActive = true,
                };
                session.Store(appUser);
                migratedCount++;
            }
            else if (string.IsNullOrEmpty(existingAppUser.UserName) || existingAppUser.UserName != userName)
            {
                // Update existing ApplicationUser if UserName was empty
                existingAppUser.UserName = userName;
                existingAppUser.NormalizedUserName = userName.ToUpperInvariant();
                session.Store(existingAppUser);
                migratedCount++;
            }

            // Append UserIdentitySetupEvent if UserView doesn't have UserName yet
            if (string.IsNullOrEmpty(user.UserName))
            {
                session.Events.Append(user.Id,
                    new UserIdentitySetupEvent(user.Id, userName, true));
            }
        }

        // Store migration marker
        session.Store(new IdentityMigrationMarker
        {
            Id = IdentityMigrationMarker.MarkerId,
            CompletedAt = DateTimeOffset.UtcNow,
            MigratedUserCount = migratedCount
        });

        await session.SaveChangesAsync(ct);

        logger.LogInformation("Identity migration completed. {Count} users migrated.", migratedCount);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public class IdentityMigrationMarker
{
    public static readonly Guid MarkerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public Guid Id { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public int MigratedUserCount { get; set; }
}
