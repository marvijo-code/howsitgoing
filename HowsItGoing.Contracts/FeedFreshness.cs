namespace HowsItGoing.Contracts;

/// <summary>
/// Renders "how long ago did the feed last load" for the header readout.
/// </summary>
/// <remarks>
/// Lives here rather than in the view model so it can be unit tested: the app project is
/// multi-targeted and the test project references only Contracts and Core.
/// </remarks>
public static class FeedFreshness
{
    /// <summary>
    /// Formats <paramref name="age"/> as a leading-separator suffix, e.g. "· 25s ago". The
    /// separator is part of the value because the readout is always rendered as a continuation of
    /// the session counts next to it.
    /// </summary>
    public static string Describe(TimeSpan age)
    {
        // A clock that has gone backwards (NTP correction, or a phone waking with a stale
        // monotonic clock) must not render "· -3s ago".
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromSeconds(10))
        {
            return "· just now";
        }

        // Truncate rather than round at every boundary, so 59.6s reads "59s ago" and never the
        // nonsensical "60s ago" that rounding to the nearest second would produce.
        if (age < TimeSpan.FromMinutes(1))
        {
            return $"· {(int)age.TotalSeconds}s ago";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"· {(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"· {(int)age.TotalHours}h ago";
        }

        return $"· {(int)age.TotalDays}d ago";
    }
}
