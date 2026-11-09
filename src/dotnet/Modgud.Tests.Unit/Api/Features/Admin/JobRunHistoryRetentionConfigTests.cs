using Modgud.Api.Features.Admin.Jobs;

namespace Modgud.Tests.Unit.Api.Features.Admin;

/// <summary>
/// B9-JS-03 — the field says "Empty = no age sweep", but a cleared value fell back to the
/// 30-day default, so a cleared field still deleted runs older than 30 days. A job that
/// was never configured keeps the default.
/// </summary>
public class JobRunHistoryRetentionConfigTests
{
    [Fact]
    public void Never_configured_uses_the_default_age() =>
        Assert.Equal(30, JobRunHistoryRetentionJob.FromParameters(null).MaxAgeDays);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_cleared_age_means_no_age_sweep(string? cleared) =>
        Assert.Null(JobRunHistoryRetentionJob.FromParameters(new Dictionary<string, object?> { ["maxAgeDays"] = cleared }).MaxAgeDays);

    /// <summary>R5 (post-merge review) — the form showed a cleared age as the 30-day default, and
    /// the next save wrote it back. The schema tells the form that a saved empty age is "none".</summary>
    [Fact]
    public void The_age_field_tells_the_form_that_empty_means_no_sweep()
    {
        var field = Assert.Single(JobRunHistoryRetentionJob.GetParameterSchema(), f => f.Key == "maxAgeDays");
        Assert.True(field.EmptyMeansNone);
        Assert.Equal(30, field.Default);
    }

    [Fact]
    public void A_set_age_is_used() =>
        Assert.Equal(7, JobRunHistoryRetentionJob.FromParameters(new Dictionary<string, object?> { ["maxAgeDays"] = 7 }).MaxAgeDays);
}
