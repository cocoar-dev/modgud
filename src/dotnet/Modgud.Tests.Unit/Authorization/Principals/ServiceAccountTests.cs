using Modgud.Authorization.Principals;

namespace Modgud.Tests.Unit.Authorization.Principals;

/// <summary>
/// Pins the small but identity-defining surface of <see cref="ServiceAccount"/>:
/// type discriminator and DisplayName resolution. Trivial today — the test
/// exists so silent regressions (e.g. forgetting to override <c>Type</c> on a
/// future refactor) get caught.
/// </summary>
public class ServiceAccountTests
{
    public class Type
    {
        [Fact]
        public void Is_stable_discriminator_string()
        {
            var sa = new ServiceAccount();
            Assert.Equal("service-account", sa.Type);
        }
    }

    public class DisplayName
    {
        [Fact]
        public void Returns_account_name_verbatim()
        {
            var sa = new ServiceAccount { AccountName = "build-bot" };
            Assert.Equal("build-bot", sa.DisplayName);
        }

        [Fact]
        public void Returns_default_empty_string_when_account_name_unset()
        {
            // ServiceAccount.AccountName defaults to empty string, not null —
            // DisplayName therefore returns "" rather than throwing.
            var sa = new ServiceAccount();
            Assert.Equal("", sa.DisplayName);
        }
    }

    public class CapabilityInterfaces
    {
        [Fact]
        public void Implements_IPrincipalWithAccount_but_not_email_or_members()
        {
            // Service accounts are explicitly NOT email-addressable (notifications
            // belong to a responsible human/group). Lock that capability gap in.
            var sa = new ServiceAccount();
            Assert.IsAssignableFrom<IPrincipalWithAccount>(sa);
            Assert.IsNotAssignableFrom<IPrincipalEmailAddressable>(sa);
            Assert.IsNotAssignableFrom<IPrincipalWithMembers>(sa);
        }
    }
}
