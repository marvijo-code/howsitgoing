using System.Text.Json;
using HowsItGoing.Contracts;

namespace HowsItGoing.Services;

/// <summary>
/// The feed as it looked at the end of the last successful refresh. It exists so the app paints
/// real data on launch instead of an empty screen while the first network call runs; the snapshot is
/// clearly aged in the header and replaced as soon as the refresh lands.
/// </summary>
public sealed record FeedSnapshot(
    DateTimeOffset SavedAt,
    SessionPageDto? Sessions,
    IReadOnlyList<BridgeNotificationDto> Notifications);

public sealed class FeedCacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _cachePath;

    public FeedCacheStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HowsItGoing",
            "feed-cache.json"))
    {
    }

    public FeedCacheStore(string cachePath)
    {
        _cachePath = cachePath;
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<FeedSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_cachePath);
            return await JsonSerializer.DeserializeAsync<FeedSnapshot>(stream, SerializerOptions, cancellationToken);
        }
        catch
        {
            // A corrupt or half-written cache is never worth failing startup over.
            return null;
        }
    }

    public async Task SaveAsync(FeedSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        try
        {
            var temporaryPath = _cachePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken);
            }

            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch
        {
            // The cache is an optimisation; losing it costs one slower launch.
        }
    }
}
