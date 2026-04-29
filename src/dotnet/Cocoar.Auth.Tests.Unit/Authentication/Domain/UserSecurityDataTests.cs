using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Tests.Unit.Authentication.Domain;

/// <summary>
/// Pins the small factory + stamp-rotation surface on <see cref="UserSecurityData"/>.
/// The stamps are the primary invalidation lever for ASP.NET Identity cookies, so a
/// "rotate" that secretly leaves the value unchanged is a security regression.
/// </summary>
public class UserSecurityDataTests
{
    public class Create
    {
        [Fact]
        public void Sets_id_to_user_id()
        {
            var userId = Guid.NewGuid();
            var data = UserSecurityData.Create(userId);

            Assert.Equal(userId, data.Id);
        }

        [Fact]
        public void Without_password_hash_leaves_hash_null()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());

            Assert.Null(data.PasswordHash);
        }

        [Fact]
        public void Stores_provided_password_hash()
        {
            var data = UserSecurityData.Create(Guid.NewGuid(), passwordHash: "AQAAAA...");

            Assert.Equal("AQAAAA...", data.PasswordHash);
        }

        [Fact]
        public void Generates_non_empty_security_and_concurrency_stamps()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());

            Assert.False(string.IsNullOrWhiteSpace(data.SecurityStamp));
            Assert.False(string.IsNullOrWhiteSpace(data.ConcurrencyStamp));
        }

        [Fact]
        public void Security_and_concurrency_stamps_are_distinct_per_instance()
        {
            var a = UserSecurityData.Create(Guid.NewGuid());
            var b = UserSecurityData.Create(Guid.NewGuid());

            Assert.NotEqual(a.SecurityStamp, b.SecurityStamp);
            Assert.NotEqual(a.ConcurrencyStamp, b.ConcurrencyStamp);
        }

        [Fact]
        public void Defaults_two_factor_flags_off()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());

            Assert.False(data.TwoFactorEnabled);
            Assert.False(data.TwoFactorExempt);
            Assert.Null(data.SecureSetupDueAt);
            Assert.Null(data.GracePeriodDaysOverride);
        }

        [Fact]
        public void Defaults_lockout_off()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());

            Assert.Equal(0, data.AccessFailedCount);
            Assert.Null(data.LockoutEnd);
        }
    }

    public class RotateAllStamps
    {
        [Fact]
        public void Replaces_security_stamp_with_a_new_value()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());
            var original = data.SecurityStamp;

            data.RotateAllStamps();

            Assert.NotEqual(original, data.SecurityStamp);
            Assert.False(string.IsNullOrWhiteSpace(data.SecurityStamp));
        }

        [Fact]
        public void Also_rotates_concurrency_stamp()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());
            var originalConcurrency = data.ConcurrencyStamp;

            data.RotateAllStamps();

            Assert.NotEqual(originalConcurrency, data.ConcurrencyStamp);
        }
    }

    public class UpdateConcurrencyStamp
    {
        [Fact]
        public void Replaces_concurrency_stamp_with_a_new_value()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());
            var original = data.ConcurrencyStamp;

            data.UpdateConcurrencyStamp();

            Assert.NotEqual(original, data.ConcurrencyStamp);
        }

        [Fact]
        public void Does_not_rotate_security_stamp()
        {
            var data = UserSecurityData.Create(Guid.NewGuid());
            var originalSecurity = data.SecurityStamp;

            data.UpdateConcurrencyStamp();

            Assert.Equal(originalSecurity, data.SecurityStamp);
        }
    }
}
