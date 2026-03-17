using System.Text.Json;
using HowsItGoing.Models;

namespace HowsItGoing.Services;

public sealed class AppSettingsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AppSettingsStore()
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HowsItGoing");

        Directory.CreateDirectory(baseDirectory);
        _settingsPath = Path.Combine(baseDirectory, "appsettings.local.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.Create(_settingsPath);
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
