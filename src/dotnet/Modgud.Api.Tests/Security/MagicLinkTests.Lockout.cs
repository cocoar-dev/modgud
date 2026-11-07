using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Email;

namespace Modgud.Api.Tests.Security;

/// <summary>
/// B5-06 — the password sign-in honours Identity's lockout (LockoutEnd, the administrative lock);
/// the magic-link sign-in checked only IsActive / IsDeleted and signed a locked account in.
/// </summary>
public partial class MagicLinkTests
{
    [Fact]
    public async Task A_magic_link_does_not_sign_in_a_locked_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var emailService = Factory.Services.GetRequiredService<InMemoryEmailService>();
        emailService.Clear();
        var anonClient = Factory.CreateDefaultClient(new CookieContainerHandler());
        await anonClient.PostAsJsonAsync("/api/account/magic-link/request", new { Email = "test@test.com" }, ct);
        var (userId, token) = ExtractMagicLinkParams(emailService.GetLastEmailTo("test@test.com")!.HtmlBody);

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(DefaultUser!.Id.ToString()))!;
            await users.SetLockoutEnabledAsync(user, true);
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
        }

        var login = await anonClient.PostAsJsonAsync("/api/account/magic-link/login", new { UserId = userId, Token = token }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
