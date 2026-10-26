using FluentAssertions;
using HowsItGoing.Contracts;

namespace HowsItGoing.Tests;

public class SessionPagingTests
{
    private static IReadOnlyList<CodexSessionSummaryDto> Sessions(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new CodexSessionSummaryDto(
                $"session-{index}",
                $"Title {index}",
                "source",
                @"C:\dev\howsitgoing",
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow.AddMinutes(-index),
                DateTimeOffset.UtcNow.AddMinutes(-index),
                false,
                CodexSessionStatus.Idle,
                null,
                null))
            .ToArray();

    [Test]
    public void Slice_returns_the_requested_window_and_the_full_count()
    {
        var page = SessionPaging.Slice(Sessions(13_000), offset: 60, limit: 30);

        page.Items.Should().HaveCount(30);
        page.Items[0].Id.Should().Be("session-60");
        page.Items[29].Id.Should().Be("session-89");
        page.TotalCount.Should().Be(13_000);
        page.Offset.Should().Be(60);
        page.Limit.Should().Be(30);
        page.HasMore.Should().BeTrue();
        page.NextOffset.Should().Be(90);
    }

    [Test]
    public void Slice_reports_the_end_of_the_feed()
    {
        var page = SessionPaging.Slice(Sessions(35), offset: 30, limit: 30);

        page.Items.Should().HaveCount(5);
        page.HasMore.Should().BeFalse();
        page.NextOffset.Should().BeNull();
    }

    [Test]
    public void Slice_beyond_the_end_is_empty_but_keeps_the_real_total()
    {
        var page = SessionPaging.Slice(Sessions(10), offset: 90, limit: 30);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(10);
        page.HasMore.Should().BeFalse();
    }

    [Test]
    public void Page_size_and_offset_are_clamped_the_same_way_on_both_sides()
    {
        SessionPaging.ClampOffset(-5).Should().Be(0);
        SessionPaging.ClampPageSize(0).Should().Be(SessionPaging.DefaultPageSize);
        SessionPaging.ClampPageSize(-20).Should().Be(SessionPaging.DefaultPageSize);
        SessionPaging.ClampPageSize(10_000).Should().Be(SessionPaging.MaxPageSize);

        var page = SessionPaging.Slice(Sessions(500), offset: -1, limit: 10_000);
        page.Items.Should().HaveCount(SessionPaging.MaxPageSize);
        page.Offset.Should().Be(0);
    }
}
