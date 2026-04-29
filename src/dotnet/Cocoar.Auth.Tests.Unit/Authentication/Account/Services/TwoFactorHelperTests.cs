using Cocoar.Auth.Authentication.Api.Account.Services;
using Cocoar.Auth.Authentication.Domain;

namespace Cocoar.Auth.Tests.Unit.Authentication.Account.Services;

/// <summary>
/// Pinning tests for the pure parts of <see cref="TwoFactorHelper"/>: the
/// configured-methods list-builder and the grace-expiration mutation. The
/// DB-bound entry points (<c>GetMethodsAsync</c>, <c>ExpireSetupGraceAsync</c>)
/// are thin wrappers that forward to these helpers; integration coverage of
/// the wrappers lives elsewhere.
/// </summary>
public class TwoFactorHelperTests
{
    public class BuildMethodsList
    {
        // Order is part of the contract — the SPA renders factor chips in the
        // returned order, and a regression that re-arranges them would change
        // the user-visible "preferred method" indicator silently.

        [Fact]
        public void No_factors_yields_empty_list()
        {
            var user = new ApplicationUser { UserName = "u" };
            var methods = TwoFactorHelper.BuildMethodsList(user, passkeyCount: 0);

            Assert.Empty(methods);
        }

        [Fact]
        public void Totp_appears_when_TwoFactorEnabled_is_set()
        {
            var user = new ApplicationUser { UserName = "u", TwoFactorEnabled = true };
            var methods = TwoFactorHelper.BuildMethodsList(user, passkeyCount: 0);

            Assert.Equal(new[] { "totp" }, methods);
        }

        [Fact]
        public void Email_appears_only_when_flag_AND_email_address_present()
        {
            // Both the EmailOtpEnabled flag AND a non-empty Email are required
            // — a flag without a destination is unusable as a factor.
            var withFlagAndEmail = new ApplicationUser
            {
                UserName = "u", EmailOtpEnabled = true, Email = "alice@example.com",
            };
            Assert.Contains("email", TwoFactorHelper.BuildMethodsList(withFlagAndEmail, 0));

            var flagButNoEmail = new ApplicationUser { UserName = "u", EmailOtpEnabled = true, Email = null };
            Assert.DoesNotContain("email", TwoFactorHelper.BuildMethodsList(flagButNoEmail, 0));

            var flagButEmptyEmail = new ApplicationUser { UserName = "u", EmailOtpEnabled = true, Email = "" };
            Assert.DoesNotContain("email", TwoFactorHelper.BuildMethodsList(flagButEmptyEmail, 0));

            var emailNoFlag = new ApplicationUser { UserName = "u", EmailOtpEnabled = false, Email = "a@x" };
            Assert.DoesNotContain("email", TwoFactorHelper.BuildMethodsList(emailNoFlag, 0));
        }

        [Fact]
        public void Passkey_appears_when_count_is_at_least_one()
        {
            var user = new ApplicationUser { UserName = "u" };

            Assert.DoesNotContain("passkey", TwoFactorHelper.BuildMethodsList(user, passkeyCount: 0));
            Assert.Contains("passkey", TwoFactorHelper.BuildMethodsList(user, passkeyCount: 1));
            Assert.Contains("passkey", TwoFactorHelper.BuildMethodsList(user, passkeyCount: 5));
        }

        [Fact]
        public void Negative_passkey_count_is_treated_as_no_passkeys()
        {
            // Defensive: callers should never hand us a negative count, but if
            // they do (e.g. a buggy query), we should not surface a passkey
            // entry. Pin the safer-side behaviour.
            var user = new ApplicationUser { UserName = "u" };
            Assert.DoesNotContain("passkey", TwoFactorHelper.BuildMethodsList(user, passkeyCount: -3));
        }

        [Fact]
        public void All_three_factors_appear_in_totp_email_passkey_order()
        {
            var user = new ApplicationUser
            {
                UserName = "u",
                TwoFactorEnabled = true,
                EmailOtpEnabled = true,
                Email = "alice@example.com",
            };

            var methods = TwoFactorHelper.BuildMethodsList(user, passkeyCount: 2);

            Assert.Equal(new[] { "totp", "email", "passkey" }, methods);
        }

        [Fact]
        public void Returned_list_is_a_fresh_instance_per_call_so_callers_can_mutate()
        {
            var user = new ApplicationUser { UserName = "u", TwoFactorEnabled = true };

            var a = TwoFactorHelper.BuildMethodsList(user, 0);
            var b = TwoFactorHelper.BuildMethodsList(user, 0);

            a.Add("custom");
            Assert.DoesNotContain("custom", b);
        }
    }

    public class TryExpireSetupGrace
    {
        [Fact]
        public void Stamps_DueAt_to_now_for_non_exempt_user_and_returns_true()
        {
            var security = UserSecurityData.Create(Guid.NewGuid());
            var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);

            var changed = TwoFactorHelper.TryExpireSetupGrace(security, now);

            Assert.True(changed);
            Assert.Equal(now, security.SecureSetupDueAt);
        }

        [Fact]
        public void Returns_false_and_does_not_mutate_for_exempt_user()
        {
            // Exempt users bypass enforcement anyway — stamping a past DueAt
            // on their record would just clutter the audit trail without
            // changing behaviour. Caller relies on the false to skip the
            // session.Store() side effect.
            var security = UserSecurityData.Create(Guid.NewGuid());
            security.TwoFactorExempt = true;
            var existingDueAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            security.SecureSetupDueAt = existingDueAt;

            var changed = TwoFactorHelper.TryExpireSetupGrace(security,
                new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));

            Assert.False(changed);
            Assert.Equal(existingDueAt, security.SecureSetupDueAt);
        }

        [Fact]
        public void Overwrites_existing_DueAt_when_called_again_for_non_exempt_user()
        {
            // Repeat-call must move the deadline forward to "now" each time,
            // not preserve a previously-stamped value. The whole point is to
            // collapse any remaining grace window.
            var security = UserSecurityData.Create(Guid.NewGuid());
            security.SecureSetupDueAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var newer = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);

            TwoFactorHelper.TryExpireSetupGrace(security, newer);

            Assert.Equal(newer, security.SecureSetupDueAt);
        }
    }
}
