using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Push;

public enum PushDecision
{
    Deliver,
    SubscriptionDisabled,
    KindFiltered,
    RepositoryFiltered,
    KeywordFiltered,
    QuietHours,
    Throttled
}

/// <summary>
/// Decides whether one feed notification reaches one subscription. Kept pure and separate from the
/// dispatcher so every rule is unit-testable without a push service or a clock.
/// </summary>
public static class WebPushPreferenceEvaluator
{
    /// <summary>UTC offsets outside this range are not real; a browser sending one gets clamped rather than crashing the send loop.</summary>
    private const int MaxUtcOffsetMinutes = 14 * 60;

    public static PushDecision Evaluate(
        WebPushPreferencesDto preferences,
        BridgeNotificationDto notification,
        DateTimeOffset? lastPushedAt,
        DateTimeOffset now)
    {
        if (!preferences.Enabled)
        {
            return PushDecision.SubscriptionDisabled;
        }

        if (preferences.Kinds is { Count: > 0 } kinds && !kinds.Contains(notification.Kind))
        {
            return PushDecision.KindFiltered;
        }

        if (preferences.Repositories is { Count: > 0 } repositories && !MatchesRepository(repositories, notification.RelatedRepository))
        {
            return PushDecision.RepositoryFiltered;
        }

        if (!string.IsNullOrWhiteSpace(preferences.Keyword) && !MatchesKeyword(preferences.Keyword, notification))
        {
            return PushDecision.KeywordFiltered;
        }

        if (IsWithinQuietHours(preferences, now))
        {
            return PushDecision.QuietHours;
        }

        if (preferences.MinIntervalSeconds > 0 &&
            lastPushedAt is { } previous &&
            now - previous < TimeSpan.FromSeconds(preferences.MinIntervalSeconds))
        {
            return PushDecision.Throttled;
        }

        return PushDecision.Deliver;
    }

    /// <summary>
    /// Substring rather than equality because the related repository is a GitHub slug for pushes but a
    /// working directory for agent events - only a substring matches both shapes of the same repo.
    /// </summary>
    private static bool MatchesRepository(IReadOnlyList<string> filters, string? relatedRepository)
    {
        if (string.IsNullOrWhiteSpace(relatedRepository))
        {
            return false;
        }

        foreach (var filter in filters)
        {
            if (!string.IsNullOrWhiteSpace(filter) &&
                relatedRepository.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesKeyword(string keyword, BridgeNotificationDto notification)
    {
        var trimmed = keyword.Trim();
        return notification.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
               notification.Message.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWithinQuietHours(WebPushPreferencesDto preferences, DateTimeOffset now)
    {
        if (preferences.QuietHoursStart is not { } start || preferences.QuietHoursEnd is not { } end)
        {
            return false;
        }

        if (start is < 0 or > 23 || end is < 0 or > 23 || start == end)
        {
            // An empty or nonsensical window is treated as "no quiet hours" rather than silencing
            // the subscription forever.
            return false;
        }

        var offset = Math.Clamp(preferences.UtcOffsetMinutes, -MaxUtcOffsetMinutes, MaxUtcOffsetMinutes);
        var hour = now.ToOffset(TimeSpan.FromMinutes(offset)).Hour;

        return start < end
            ? hour >= start && hour < end
            : hour >= start || hour < end; // wraps past midnight, e.g. 22:00 -> 07:00
    }
}
