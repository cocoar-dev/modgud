using Cocoar.Auth.Infrastructure.Email;

namespace Cocoar.Auth.Tests.Unit.Infrastructure.Email;

/// <summary>
/// Pins the Mustache-style placeholder substitution behaviour of
/// <see cref="EmailTemplateStore.Render"/>. The contract MUST stay compatible
/// with Postmark's <c>{{Variable}}</c> syntax — drift breaks every transactional
/// email the system sends.
/// </summary>
public class EmailTemplateStoreTests
{
    public class Render
    {
        [Fact]
        public void Substitutes_placeholders_in_subject_and_body()
        {
            var (subject, body) = EmailTemplateStore.Render(EmailTemplate.EmailOtp, new()
            {
                ["AppName"] = "Cocoar",
                ["DisplayName"] = "Alice",
                ["Code"] = "123456",
                ["ExpirationMinutes"] = "10",
            });

            Assert.Equal("Cocoar — Anmelde-Code", subject);
            Assert.Contains("Hallo Alice,", body);
            Assert.Contains("123456", body);
            Assert.Contains("10 Minuten", body);
        }

        [Fact]
        public void Leaves_unknown_placeholders_untouched()
        {
            // The Render contract: missing keys MUST stay literal so an
            // operator-visible {{Foo}} in the email is the smoke signal —
            // silent omission would hide the bug downstream.
            var (subject, body) = EmailTemplateStore.Render(EmailTemplate.MagicLink, new()
            {
                ["AppName"] = "Cocoar",
                // DisplayName, ActionUrl, ExpirationMinutes intentionally missing
            });

            Assert.Equal("Cocoar — Anmelde-Link", subject);
            Assert.Contains("{{DisplayName}}", body);
            Assert.Contains("{{ActionUrl}}", body);
            Assert.Contains("{{ExpirationMinutes}}", body);
        }

        [Fact]
        public void Substitutes_same_placeholder_in_subject_and_body()
        {
            // {{AppName}} appears in both the subject and body of EmailVerification —
            // both occurrences must be replaced from a single dictionary entry.
            var (subject, body) = EmailTemplateStore.Render(EmailTemplate.EmailVerification, new()
            {
                ["AppName"] = "MyApp",
                ["DisplayName"] = "Bob",
                ["ActionUrl"] = "https://example/verify",
                ["ExpirationHours"] = "24",
            });

            Assert.Contains("MyApp", subject);
            Assert.Contains("MyApp-Konto", body);
            Assert.DoesNotContain("{{AppName}}", subject);
            Assert.DoesNotContain("{{AppName}}", body);
        }

        [Fact]
        public void Substitutes_change_request_fields_into_subject()
        {
            var (subject, body) = EmailTemplateStore.Render(EmailTemplate.AdminChangeRequestNotification, new()
            {
                ["AppName"] = "Cocoar",
                ["Field"] = "Email",
                ["RequestingUser"] = "alice@example.com",
                ["OldValue"] = "old@example.com",
                ["NewValue"] = "new@example.com",
                ["ActionUrl"] = "https://admin/review",
            });

            Assert.Equal("Cocoar — Neue Änderungsanfrage: Email", subject);
            Assert.Contains("alice@example.com", body);
            Assert.Contains("old@example.com", body);
            Assert.Contains("new@example.com", body);
            Assert.Contains("https://admin/review", body);
        }

        [Theory]
        [InlineData(EmailTemplate.EmailOtp)]
        [InlineData(EmailTemplate.MagicLink)]
        [InlineData(EmailTemplate.PasswordReset)]
        [InlineData(EmailTemplate.EmailVerification)]
        [InlineData(EmailTemplate.AdminChangeRequestNotification)]
        [InlineData(EmailTemplate.ChangeRequestApproved)]
        [InlineData(EmailTemplate.ChangeRequestRejected)]
        public void Every_known_template_is_renderable(EmailTemplate template)
        {
            // Smoke: each enum value MUST have a registered template entry —
            // adding a value to the enum without a template would silently throw
            // at first send. This catches the omission at unit-test time.
            var (subject, body) = EmailTemplateStore.Render(template, new());

            Assert.False(string.IsNullOrWhiteSpace(subject));
            Assert.False(string.IsNullOrWhiteSpace(body));
        }

        [Fact]
        public void Throws_for_unknown_template_value()
        {
            // Cast an out-of-range int to the enum to simulate a template lookup miss.
            var unknown = (EmailTemplate)9999;

            var ex = Assert.Throws<ArgumentException>(() => EmailTemplateStore.Render(unknown, new()));
            Assert.Contains("Unknown email template", ex.Message);
        }

        [Fact]
        public void Empty_model_keeps_all_placeholders_intact()
        {
            // No replacements requested = every {{X}} should still appear literally.
            var (subject, body) = EmailTemplateStore.Render(EmailTemplate.PasswordReset, new());

            Assert.Contains("{{AppName}}", subject);
            Assert.Contains("{{DisplayName}}", body);
            Assert.Contains("{{ActionUrl}}", body);
            Assert.Contains("{{ExpirationMinutes}}", body);
        }
    }
}
