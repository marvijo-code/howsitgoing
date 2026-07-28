using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Push;

/// <summary>
/// Fans every new feed notification out to the browser subscriptions whose preferences accept it.
///
/// Runs off <see cref="NotificationBroadcaster"/> rather than polling, so a push lands as soon as the
/// notification is recorded - the whole point of push over the app's 30s refresh.
/// </summary>
public sealed class WebPushDispatcher : BackgroundService
{
    private readonly NotificationBroadcaster _broadcaster;
    private readonly WebPushSubscriptionStore _store;
    private readonly WebPushSender _sender;
    private readonly ILogger<WebPushDispatcher> _logger;

    public WebPushDispatcher(
        NotificationBroadcaster broadcaster,
        WebPushSubscriptionStore store,
        WebPushSender sender,
        ILogger<WebPushDispatcher> logger)
    {
        _broadcaster = broadcaster;
        _store = store;
        _sender = sender;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in _broadcaster.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await DispatchAsync(notification, stoppingToken);
                if (result.Delivered > 0 || result.Removed > 0)
                {
                    _logger.LogInformation(
                        "Pushed {Kind} to {Delivered} subscription(s); {Filtered} filtered, {Removed} removed.",
                        notification.Kind,
                        result.Delivered,
                        result.Filtered,
                        result.Removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad notification must never take the dispatcher down for the rest.
                _logger.LogWarning(ex, "Web push dispatch failed for notification {Id}.", notification.Id);
            }
        }
    }

    public async Task<WebPushSendResultDto> DispatchAsync(
        BridgeNotificationDto notification,
        CancellationToken cancellationToken,
        string? onlyEndpoint = null,
        bool ignorePreferences = false)
    {
        var subscriptions = await _store.GetAllAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var delivered = 0;
        var filtered = 0;
        var removed = 0;
        var errors = new List<string>();

        foreach (var subscription in subscriptions)
        {
            if (onlyEndpoint is not null && !string.Equals(subscription.Endpoint, onlyEndpoint, StringComparison.Ordinal))
            {
                continue;
            }

            if (subscription.ExpiresAt is { } expiry && expiry <= now)
            {
                await _store.RemoveAsync(subscription.Endpoint, cancellationToken);
                removed++;
                continue;
            }

            if (!ignorePreferences)
            {
                var decision = WebPushPreferenceEvaluator.Evaluate(
                    subscription.Preferences,
                    notification,
                    subscription.LastPushedAt,
                    now);

                if (decision != PushDecision.Deliver)
                {
                    _logger.LogDebug(
                        "Skipped push to {Endpoint}: {Decision}.",
                        WebPushSender.Describe(subscription.Endpoint),
                        decision);
                    filtered++;
                    continue;
                }
            }

            var result = await _sender.SendAsync(subscription, notification, cancellationToken);
            switch (result.Status)
            {
                case WebPushDeliveryStatus.Delivered:
                    delivered++;
                    await _store.MarkPushedAsync(subscription.Endpoint, now, cancellationToken);
                    break;

                case WebPushDeliveryStatus.Gone:
                    // The browser revoked it. Keeping it would retry forever against a dead endpoint.
                    await _store.RemoveAsync(subscription.Endpoint, cancellationToken);
                    removed++;
                    break;

                default:
                    errors.Add(result.Error ?? "Unknown push failure.");
                    break;
            }
        }

        return new WebPushSendResultDto(delivered, filtered, removed, errors);
    }
}
