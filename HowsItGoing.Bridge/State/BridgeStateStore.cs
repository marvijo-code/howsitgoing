using System.Text.Json;
using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.State;

public sealed class BridgeStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private PersistentBridgeState _state;

    public BridgeStateStore()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HowsItGoing",
            "Bridge");

        Directory.CreateDirectory(baseDirectory);
        _statePath = Path.Combine(baseDirectory, "state.json");
        _state = LoadState();
    }

    public async Task<IReadOnlyList<BridgeNotificationDto>> GetNotificationsAsync(DateTimeOffset? since, int limit, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IEnumerable<BridgeNotificationDto> query = _state.Notifications.OrderByDescending(x => x.OccurredAt);
            if (since is { } cutoff)
            {
                query = query.Where(x => x.OccurredAt > cutoff);
            }

            return query.Take(Math.Clamp(limit, 1, 200)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryRecordCompletedThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_state.NotifiedCompletedThreadIds.Add(threadId))
            {
                return false;
            }

            await PersistLockedAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetLastGitHubEventIdAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.LastGitHubEventId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetLastGitHubEventIdAsync(string? eventId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state.LastGitHubEventId = eventId;
            await PersistLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddNotificationAsync(BridgeNotificationDto notification, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state.Notifications.Any(x => x.Id == notification.Id))
            {
                return;
            }

            _state.Notifications.Add(notification);
            _state.Notifications = _state.Notifications
                .OrderByDescending(x => x.OccurredAt)
                .Take(500)
                .OrderBy(x => x.OccurredAt)
                .ToList();

            await PersistLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PersistentBridgeState LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return new PersistentBridgeState();
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<PersistentBridgeState>(json) ?? new PersistentBridgeState();
        }
        catch
        {
            return new PersistentBridgeState();
        }
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, _state, cancellationToken: cancellationToken);
    }

    private sealed class PersistentBridgeState
    {
        public HashSet<string> NotifiedCompletedThreadIds { get; set; } = [];

        public string? LastGitHubEventId { get; set; }

        public List<BridgeNotificationDto> Notifications { get; set; } = [];
    }
}
