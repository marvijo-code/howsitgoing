using FluentAssertions;
using HowsItGoing.Contracts;
using NUnit.Framework;

namespace HowsItGoing.Tests;

/// <summary>
/// The transcript window is counted from the end of the conversation: opening a thread must land on
/// the newest turn, and "load earlier" must step backwards without skipping or repeating a message.
/// </summary>
[TestFixture]
public sealed class SessionThreadPagingTests
{
    [Test]
    public void Newest_window_ends_at_the_last_message()
    {
        var (start, count) = SessionThreadPaging.SelectWindow(totalCount: 100, limit: 40, before: null);

        start.Should().Be(60);
        count.Should().Be(40);
    }

    [Test]
    public void Earlier_window_steps_back_from_the_given_index()
    {
        var (start, count) = SessionThreadPaging.SelectWindow(totalCount: 100, limit: 40, before: 60);

        start.Should().Be(20);
        count.Should().Be(40);
    }

    [Test]
    public void First_window_is_clamped_to_the_start()
    {
        var (start, count) = SessionThreadPaging.SelectWindow(totalCount: 25, limit: 40, before: null);

        start.Should().Be(0);
        count.Should().Be(25);
    }

    [Test]
    public void Short_transcript_returns_everything()
    {
        var (start, count) = SessionThreadPaging.SelectWindow(totalCount: 3, limit: 40, before: null);

        start.Should().Be(0);
        count.Should().Be(3);
    }

    [Test]
    public void Stale_index_from_a_sleeping_phone_clamps_into_range()
    {
        var (start, count) = SessionThreadPaging.SelectWindow(totalCount: 10, limit: 5, before: 999);

        start.Should().Be(5);
        count.Should().Be(5);
    }

    [Test]
    public void Empty_transcript_returns_no_messages()
    {
        var (start, count) = SessionThreadPaging.SelectWindow(totalCount: 0, limit: 40, before: null);

        start.Should().Be(0);
        count.Should().Be(0);
    }

    [TestCase(0, SessionThreadPaging.DefaultPageSize)]
    [TestCase(-5, SessionThreadPaging.DefaultPageSize)]
    [TestCase(5000, SessionThreadPaging.MaxPageSize)]
    public void Page_size_is_clamped(int requested, int expected)
    {
        SessionThreadPaging.ClampPageSize(requested).Should().Be(expected);
    }
}
