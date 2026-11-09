using Modgud.Application.Inbox;

namespace Modgud.Tests.Unit.Application.Inbox;

/// <summary>B3-EI-07 — retention settings the job cannot run with are refused.</summary>
public sealed class InboxRetentionSettingsValidationTests
{
    [Fact]
    public void Defaults_are_valid() => Assert.Null(new InboxRetentionSettings().Validate());

    [Fact]
    public void Null_means_never_and_is_valid()
    {
        var settings = new InboxRetentionSettings();
        settings.AdminChangeRequest.HardDeleteDaysAfterDismissed = null;
        Assert.Null(settings.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_days_are_refused(int days)
    {
        var settings = new InboxRetentionSettings();
        settings.ChangeRequestFeedback.MaxUnreadDays = days;
        Assert.NotNull(settings.Validate());
    }

    [Fact]
    public void A_missing_section_is_refused()
    {
        var settings = new InboxRetentionSettings { ScheduledJobFeedback = null! };
        Assert.NotNull(settings.Validate());
    }
}
