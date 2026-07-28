using FluentAssertions;
using HowsItGoing.Contracts;

namespace HowsItGoing.Tests;

public class FeedFreshnessTests
{
    [TestCase(0, "· just now")]
    [TestCase(9, "· just now")]
    [TestCase(10, "· 10s ago")]
    [TestCase(59, "· 59s ago")]
    [TestCase(60, "· 1m ago")]
    [TestCase(3599, "· 59m ago")]
    [TestCase(3600, "· 1h ago")]
    [TestCase(86_399, "· 23h ago")]
    [TestCase(86_400, "· 1d ago")]
    public void Describe_formats_each_band(int ageSeconds, string expected) =>
        FeedFreshness.Describe(TimeSpan.FromSeconds(ageSeconds)).Should().Be(expected);

    [Test]
    public void Describe_truncates_rather_than_rounding_up_across_a_boundary() =>
        // Rounding would render the impossible "60s ago" just before the minute band takes over.
        FeedFreshness.Describe(TimeSpan.FromMilliseconds(59_600)).Should().Be("· 59s ago");

    [Test]
    public void Describe_clamps_a_backwards_clock_to_just_now() =>
        // A phone waking up can hand back a negative age; "· -3s ago" must never reach the header.
        FeedFreshness.Describe(TimeSpan.FromSeconds(-3)).Should().Be("· just now");
}
