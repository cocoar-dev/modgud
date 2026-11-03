using System.Diagnostics;
using System.Text.Json;
using Marten;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Authentication.RealmSettings;
using Modgud.Application.DTOs.RealmSettings;
using Modgud.Application.Inbox;
using BuildingBlocks.Helper;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// Opt-in browser regression against the real HTTP/SignalR host and postgres-dev.
/// No Docker lifecycle, API mocks or production credentials.
/// </summary>
public class LiveFixBrowserTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Real_profile_deletion_and_service_account_disclosure()
    {
        var project = Environment.GetEnvironmentVariable("MODGUD_TEST_BROWSER_PROJECT");
        Assert.SkipWhen(project is null, "Set MODGUD_TEST_BROWSER_PROJECT and the explicit postgres-dev connection to opt in.");
        var ct = TestContext.Current.CancellationToken;
        var connection = TestPostgres.ValidateDevelopmentConnectionString(
            Environment.GetEnvironmentVariable("MODGUD_TEST_POSTGRES_CONNECTION_STRING")!);
        var actual = Factory.Services.GetRequiredService<StartUpConfiguration>().DbSettings.ConnectionString;
        Assert.True(string.Equals(connection, TestPostgres.ValidateDevelopmentConnectionString(actual), StringComparison.Ordinal),
            "The resolved host database differs from the validated development target.");

        // The SA disclosure scenario is not an administration-MFA test. Arrange
        // an explicitly Single administration policy before the real logins, so
        // its route guard does not correctly send these password-only users away.
        using (var scope = Factory.Services.CreateScope())
        {
            var policy = await scope.ServiceProvider.GetRequiredService<IRealmSettingsService>()
                .PatchAsync(new UpdateRealmSettingsDto
                {
                    SignIn = new UpdateSignInPolicyDto { MinimumLevel = "Single", AdministrationMinimumLevel = "Single" },
                }, ct);
            Assert.False(policy.IsError);
        }

        var creator = await Factory.CreateTestUserWithIdentityAsync("Live", "Creator", "LC", "creator@modgud.test");
        var reader = await Factory.CreateTestUserWithIdentityAsync("Live", "Reader", "LR", "reader@modgud.test");
        var profile = await Factory.CreateTestUserWithIdentityAsync("Live", "Profile", "LP", "profile@modgud.test");
        var creatorRole = await Factory.CreateTestRoleAsync("Live creator",
            [("service-account", "read"), ("service-account", "write"), ("oauth-scope", "read"), ("app", "read")]);
        // B10-UI01: the reader must reach its own surface without an unrelated
        // OAuth-scope grant. Server read/write gates remain the authority.
        var readerRole = await Factory.CreateTestRoleAsync("Live reader",
            [("service-account", "read")]);
        await Factory.CreateTestGroupAsync("Live creators", [creator.Id], [creatorRole.Id]);
        await Factory.CreateTestGroupAsync("Live readers", [reader.Id], [readerRole.Id]);
        await Factory.WaitForProjectionsAsync();

        // Profile login itself is real. Only its persisted grace deadline is
        // arranged out-of-band; policy changes happen after the SA scenario.
        await using (var session = GetTenantedDocumentSession())
        {
            var security = await session.LoadAsync<UserSecurityData>(profile.Id, ct) ?? UserSecurityData.Create(profile.Id);
            security.SecureSetupDueAt = DateTime.UtcNow.AddDays(-30);
            session.Store(security);
            await session.SaveChangesAsync(ct);
        }

        // A real persisted notification makes stale-cache removal observable;
        // an initially empty inbox would not prove that a failed reload clears it.
        IReadOnlyList<Guid> inboxIds;
        using (var scope = Factory.Services.CreateScope())
        {
            inboxIds = await scope.ServiceProvider.GetRequiredService<IInboxNotifier>()
                .NotifyAsync(InboxKind.ChangeRequestApproved, [profile.Id],
                    "Reconnect regression notification", ct: ct);
        }
        Assert.Single(inboxIds);
        await Factory.WaitForProjectionsAsync();

        var address = Factory.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var target = new Uri(address);
        Assert.Equal("127.0.0.1", target.Host);
        var fixtureFile = Path.Combine(Path.GetTempPath(), $"modgud-live-{Guid.NewGuid():N}.json");
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(fixtureFile, fileOptions))
            {
                await JsonSerializer.SerializeAsync(file, new
                {
                    BaseUrl = address,
                    InboxId = ShortGuid.Encode(inboxIds[0]),
                    Creator = new { UserName = "lc", Password = DefaultPassword },
                    Reader = new { UserName = "lr", Password = DefaultPassword },
                    Profile = new { UserName = "lp", Password = DefaultPassword },
                    Admin = new { UserName = "tu", Password = DefaultPassword },
                }, cancellationToken: ct);
            }

            var start = new ProcessStartInfo("pnpm")
            {
                WorkingDirectory = project!, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("playwright");
            start.ArgumentList.Add("test");
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add("playwright.live-fixes.config.ts");
            start.Environment["MODGUD_LIVE_FIXTURE"] = fixtureFile;
            // Playwright's automatic error-context DOM snapshot could otherwise
            // persist the one-time secret even with traces/screenshots disabled.
            start.Environment["PLAYWRIGHT_NO_COPY_PROMPT"] = "1";
            using var process = Process.Start(start)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(ct);
                var error = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(4), ct);
                Assert.True(process.ExitCode == 0, (await output) + (await error));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            File.Delete(fixtureFile);
        }
    }
}
