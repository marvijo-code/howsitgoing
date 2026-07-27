using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Text.Json;

namespace HowsItGoing.Services;

public sealed class SharedStoreOptions
{
    public const string SectionName = "SharedStore";

    public string? ConnectionString { get; set; }

    public int CommandPollSeconds { get; set; } = 5;

    public int SyncIntervalSeconds { get; set; } = 20;

    public int CommandStartTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// The shared store is a direct MySQL connection, which needs a raw TCP socket. A browser
    /// (WASM) head has no such socket, so the web app talks to the bridge over HTTP only.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString) && !OperatingSystem.IsBrowser();

    public static SharedStoreOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        return new SharedStoreOptions
        {
            ConnectionString = FirstNonEmpty(
                section["ConnectionString"],
                configuration[$"{SectionName}:ConnectionString"],
                Environment.GetEnvironmentVariable("HOWSITGOING_SHAREDSTORE_CONNECTION_STRING"),
                Environment.GetEnvironmentVariable($"{SectionName}__ConnectionString")),
            CommandPollSeconds = ParseInt(section["CommandPollSeconds"], fallback: 5),
            SyncIntervalSeconds = ParseInt(section["SyncIntervalSeconds"], fallback: 20),
            CommandStartTimeoutSeconds = ParseInt(section["CommandStartTimeoutSeconds"], fallback: 45)
        };
    }

    public static SharedStoreOptions FromLocalJsonFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new SharedStoreOptions();
        }

        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch
        {
            return new SharedStoreOptions();
        }
    }

    /// <summary>
    /// Reads the SharedStore section from a json file embedded in <paramref name="assembly"/>.
    /// On Android, loose Content files are packaged as APK assets and are not reachable through
    /// <see cref="File.Exists(string)"/>, so the app embeds appsettings.Local.json instead.
    /// </summary>
    public static SharedStoreOptions FromEmbeddedResource(Assembly assembly, string fileName)
    {
        try
        {
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return new SharedStoreOptions();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return new SharedStoreOptions();
            }

            using var reader = new StreamReader(stream);
            return FromJson(reader.ReadToEnd());
        }
        catch
        {
            return new SharedStoreOptions();
        }
    }

    public static SharedStoreOptions FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(SectionName, out var sharedStoreSection))
            {
                return new SharedStoreOptions();
            }

            return new SharedStoreOptions
            {
                ConnectionString = sharedStoreSection.TryGetProperty("ConnectionString", out var connectionString)
                    ? connectionString.GetString()
                    : null,
                CommandPollSeconds = sharedStoreSection.TryGetProperty("CommandPollSeconds", out var commandPollSeconds) &&
                                     commandPollSeconds.TryGetInt32(out var parsedPollSeconds) &&
                                     parsedPollSeconds > 0
                    ? parsedPollSeconds
                    : 5,
                SyncIntervalSeconds = sharedStoreSection.TryGetProperty("SyncIntervalSeconds", out var syncIntervalSeconds) &&
                                      syncIntervalSeconds.TryGetInt32(out var parsedSyncInterval) &&
                                      parsedSyncInterval > 0
                    ? parsedSyncInterval
                    : 20,
                CommandStartTimeoutSeconds = sharedStoreSection.TryGetProperty("CommandStartTimeoutSeconds", out var commandStartTimeoutSeconds) &&
                                             commandStartTimeoutSeconds.TryGetInt32(out var parsedStartTimeout) &&
                                             parsedStartTimeout > 0
                    ? parsedStartTimeout
                    : 45
            };
        }
        catch
        {
            return new SharedStoreOptions();
        }
    }

    public SharedStoreOptions Merge(SharedStoreOptions overrides) =>
        new()
        {
            ConnectionString = FirstNonEmpty(overrides.ConnectionString, ConnectionString),
            CommandPollSeconds = overrides.CommandPollSeconds > 0 ? overrides.CommandPollSeconds : CommandPollSeconds,
            SyncIntervalSeconds = overrides.SyncIntervalSeconds > 0 ? overrides.SyncIntervalSeconds : SyncIntervalSeconds,
            CommandStartTimeoutSeconds = overrides.CommandStartTimeoutSeconds > 0
                ? overrides.CommandStartTimeoutSeconds
                : CommandStartTimeoutSeconds
        };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
