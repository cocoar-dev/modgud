using Modgud.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modgud.Tests.Unit.Infrastructure.Email;

/// <summary>
/// Pins the capture-and-recall behaviour of <see cref="InMemoryEmailService"/>.
/// E2E tests rely on this service to assert "did the system actually send the
/// password-reset email?" — so the queue ordering, per-recipient fan-out, and
/// case-insensitive lookup MUST stay stable.
/// </summary>
public class InMemoryEmailServiceTests
{
    private static InMemoryEmailService NewService() =>
        new(NullLogger<InMemoryEmailService>.Instance);

    public class SendEmail
    {
        [Fact]
        public async Task Captures_a_single_email()
        {
            var svc = NewService();

            await svc.SendEmailAsync("to@example.com", "Subject", "<p>Body</p>");

            var emails = svc.GetSentEmails();
            Assert.Single(emails);
            Assert.Equal("to@example.com", emails[0].To);
            Assert.Equal("Subject", emails[0].Subject);
            Assert.Equal("<p>Body</p>", emails[0].HtmlBody);
        }

        [Fact]
        public async Task GetSentEmails_returns_newest_first()
        {
            var svc = NewService();

            await svc.SendEmailAsync("a@example.com", "First", "1");
            await svc.SendEmailAsync("b@example.com", "Second", "2");
            await svc.SendEmailAsync("c@example.com", "Third", "3");

            var emails = svc.GetSentEmails();
            Assert.Equal(3, emails.Count);
            Assert.Equal("Third", emails[0].Subject);
            Assert.Equal("Second", emails[1].Subject);
            Assert.Equal("First", emails[2].Subject);
        }
    }

    public class SendTemplatedEmail
    {
        [Fact]
        public async Task Renders_template_and_captures_subject_and_body()
        {
            var svc = NewService();

            await svc.SendTemplatedEmailAsync("to@example.com", EmailTemplate.EmailOtp, new()
            {
                ["AppName"] = "Cocoar",
                ["DisplayName"] = "Alice",
                ["Code"] = "123456",
                ["ExpirationMinutes"] = "10",
            });

            var email = Assert.Single(svc.GetSentEmails());
            Assert.Equal("to@example.com", email.To);
            Assert.Equal("Cocoar — Anmelde-Code", email.Subject);
            Assert.Contains("Alice", email.HtmlBody);
            Assert.Contains("123456", email.HtmlBody);
        }
    }

    public class SendTemplatedEmailMultiRecipient
    {
        [Fact]
        public async Task Stores_one_entry_per_recipient_so_GetLastEmailTo_works()
        {
            var svc = NewService();

            await svc.SendTemplatedEmailAsync(
                new[] { "alice@example.com", "bob@example.com" },
                EmailTemplate.ChangeRequestApproved,
                new()
                {
                    ["AppName"] = "Cocoar",
                    ["DisplayName"] = "Recipient",
                    ["Field"] = "Email",
                    ["NewValue"] = "x@y.com",
                });

            Assert.Equal(2, svc.GetSentEmails().Count);
            Assert.NotNull(svc.GetLastEmailTo("alice@example.com"));
            Assert.NotNull(svc.GetLastEmailTo("bob@example.com"));
        }

        [Fact]
        public async Task Skips_null_or_whitespace_addresses()
        {
            var svc = NewService();

            await svc.SendTemplatedEmailAsync(
                new[] { "alice@example.com", "", "   " },
                EmailTemplate.ChangeRequestApproved,
                new() { ["Field"] = "X", ["NewValue"] = "Y" });

            var email = Assert.Single(svc.GetSentEmails());
            Assert.Equal("alice@example.com", email.To);
        }

        [Fact]
        public async Task No_recipients_means_no_emails_captured()
        {
            var svc = NewService();

            await svc.SendTemplatedEmailAsync(
                Array.Empty<string>(),
                EmailTemplate.ChangeRequestApproved,
                new());

            Assert.Empty(svc.GetSentEmails());
        }
    }

    public class GetLastEmailTo
    {
        [Fact]
        public async Task Returns_most_recent_email_for_address()
        {
            var svc = NewService();

            await svc.SendEmailAsync("alice@example.com", "First", "1");
            await svc.SendEmailAsync("bob@example.com", "Other", "x");
            await svc.SendEmailAsync("alice@example.com", "Second", "2");

            var last = svc.GetLastEmailTo("alice@example.com");
            Assert.NotNull(last);
            Assert.Equal("Second", last!.Subject);
        }

        [Fact]
        public async Task Lookup_is_case_insensitive()
        {
            var svc = NewService();

            await svc.SendEmailAsync("Alice@Example.COM", "Hi", "body");

            Assert.NotNull(svc.GetLastEmailTo("alice@example.com"));
            Assert.NotNull(svc.GetLastEmailTo("ALICE@EXAMPLE.COM"));
        }

        [Fact]
        public void Returns_null_when_no_match()
        {
            var svc = NewService();
            Assert.Null(svc.GetLastEmailTo("nobody@example.com"));
        }
    }

    public class Clear
    {
        [Fact]
        public async Task Empties_the_captured_queue()
        {
            var svc = NewService();
            await svc.SendEmailAsync("a@b.c", "s", "b");
            Assert.Single(svc.GetSentEmails());

            svc.Clear();

            Assert.Empty(svc.GetSentEmails());
        }
    }

    public class SentEmailRecord
    {
        [Fact]
        public void Is_value_equal_when_all_fields_match()
        {
            var ts = DateTimeOffset.UtcNow;
            var a = new SentEmail("to@x", "s", "b", ts);
            var b = new SentEmail("to@x", "s", "b", ts);
            Assert.Equal(a, b);
        }
    }
}
