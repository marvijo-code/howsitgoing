using FluentAssertions;
using HowsItGoing.Contracts;

namespace HowsItGoing.Tests;

public class NotificationChannelKeyTests
{
    [Test]
    public void Same_settings_always_produce_the_same_key()
    {
        var first = NotificationChannelKey.For("default", true, "Default");
        var second = NotificationChannelKey.For("default", true, "Default");

        first.Should().Be(second);
        first.Should().HaveLength(8);
    }

    [Test]
    public void A_different_sound_vibration_or_importance_changes_the_key()
    {
        var baseline = NotificationChannelKey.For("default", true, "Default");

        NotificationChannelKey.For("none", true, "Default").Should().NotBe(baseline);
        NotificationChannelKey.For("default", false, "Default").Should().NotBe(baseline);
        NotificationChannelKey.For("default", true, "High").Should().NotBe(baseline);
    }

    [Test]
    public void Surrounding_whitespace_and_null_are_normalised()
    {
        NotificationChannelKey.For(" default ", true, "Default")
            .Should().Be(NotificationChannelKey.For("default", true, "Default"));

        NotificationChannelKey.For(null, true, "Default")
            .Should().Be(NotificationChannelKey.For("", true, "Default"));
    }
}
