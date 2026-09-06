using Modgud.Authentication.Domain;

namespace Modgud.Tests.Unit.Authentication.Domain;

public class ClientSessionTests
{
    [Fact]
    public void Touch_slides_idle_expiry_but_caps_it_at_absolute_expiry()
    {
        var created = DateTimeOffset.UtcNow;
        var session = new ClientSession
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ClientId = "acmelist-ios",
            OAuthApplicationId = Guid.NewGuid().ToString(),
            AuthorizationId = Guid.NewGuid().ToString(),
            CreatedAt = created,
            LastActiveAt = created,
            ExpiresAt = created.AddDays(30),
            AbsoluteExpiresAt = created.AddDays(3650),
        };

        session.Touch(created.AddDays(3640), TimeSpan.FromDays(30));

        Assert.Equal(created.AddDays(3640), session.LastActiveAt);
        Assert.Equal(session.AbsoluteExpiresAt, session.ExpiresAt);
        Assert.True(session.IsActive(created.AddDays(3649)));
        Assert.False(session.IsActive(session.AbsoluteExpiresAt));
    }
}
