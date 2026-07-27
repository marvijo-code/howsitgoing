using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Security;

/// <summary>
/// Persists the devices that have completed pairing.
///
/// Only a SHA-256 hash of each device token is stored, so the file cannot be used to authenticate
/// if it leaks. The plaintext token exists exactly once, in the pairing response.
/// </summary>
public sealed class PairedDeviceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storePath;
    private PersistentDeviceState? _state;

    public PairedDeviceStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    /// <param name="localAppDataPath">
    /// Root to store under. Injectable so tests never write to the real app data directory -
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves through the shell,
    /// not the LOCALAPPDATA variable, so it cannot be redirected with an environment override.
    /// </param>
    public PairedDeviceStore(string localAppDataPath)
    {
        var baseDirectory = Path.Combine(localAppDataPath, "HowsItGoing");
        Directory.CreateDirectory(baseDirectory);
        _storePath = Path.Combine(baseDirectory, "paired-devices.json");
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<PairingRedeemResponse> AddAsync(string deviceName, CancellationToken cancellationToken)
    {
        // 32 bytes: this is the long-lived credential, unlike the 8-character pairing code.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var device = new PairedDevice
        {
            DeviceId = Guid.NewGuid().ToString("N"),
            DeviceName = deviceName,
            TokenHash = HashToken(token),
            PairedAt = DateTimeOffset.UtcNow
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken);
            state.Devices.Add(device);
            await SaveUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        return new PairingRedeemResponse(token, device.DeviceId, device.DeviceName, device.PairedAt);
    }

    /// <summary>
    /// Returns the matching device, comparing in fixed time so a wrong token cannot be narrowed
    /// down by timing. Also stamps LastSeenAt.
    /// </summary>
    public async Task<PairedDeviceDto?> TryAuthenticateAsync(string presentedToken, CancellationToken cancellationToken)
    {
        var presentedHash = Encoding.UTF8.GetBytes(HashToken(presentedToken));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken);
            foreach (var device in state.Devices)
            {
                var storedHash = Encoding.UTF8.GetBytes(device.TokenHash);
                if (!CryptographicOperations.FixedTimeEquals(presentedHash, storedHash))
                {
                    continue;
                }

                device.LastSeenAt = DateTimeOffset.UtcNow;
                await SaveUnsafeAsync(cancellationToken);
                return new PairedDeviceDto(device.DeviceId, device.DeviceName, device.PairedAt, device.LastSeenAt);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PairedDeviceDto>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken);
            return state.Devices
                .Select(device => new PairedDeviceDto(device.DeviceId, device.DeviceName, device.PairedAt, device.LastSeenAt))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RevokeAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken);
            var removed = state.Devices.RemoveAll(device =>
                string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                await SaveUnsafeAsync(cancellationToken);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PersistentDeviceState> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_state is not null)
        {
            return _state;
        }

        if (!File.Exists(_storePath))
        {
            _state = new PersistentDeviceState();
            return _state;
        }

        try
        {
            await using var stream = File.OpenRead(_storePath);
            _state = await JsonSerializer.DeserializeAsync<PersistentDeviceState>(stream, cancellationToken: cancellationToken)
                ?? new PersistentDeviceState();
        }
        catch (JsonException)
        {
            _state = new PersistentDeviceState();
        }

        return _state;
    }

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_storePath);
        await JsonSerializer.SerializeAsync(stream, _state, cancellationToken: cancellationToken);
    }

    internal sealed class PersistentDeviceState
    {
        public List<PairedDevice> Devices { get; set; } = [];
    }

    internal sealed class PairedDevice
    {
        public string DeviceId { get; set; } = string.Empty;

        public string DeviceName { get; set; } = string.Empty;

        public string TokenHash { get; set; } = string.Empty;

        public DateTimeOffset PairedAt { get; set; }

        public DateTimeOffset? LastSeenAt { get; set; }
    }
}
