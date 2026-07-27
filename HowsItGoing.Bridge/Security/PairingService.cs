using System.Security.Cryptography;
using System.Text;
using HowsItGoing.Contracts;

namespace HowsItGoing.Bridge.Security;

/// <summary>
/// Device pairing via a short code the user reads off the bridge host and types into the app.
///
/// An 8-character code is only ~10^12 of entropy, which is not enough to stand up as a permanent
/// credential. Three properties make it safe here, and all three matter:
///
/// <list type="number">
///   <item>It only exists while the user has explicitly started pairing. With no active code the
///     redeem endpoint rejects everything, so there is no permanently open guessing surface.</item>
///   <item>It expires (5 minutes) and is destroyed on first success.</item>
///   <item>A small number of wrong guesses burns it. At 5 attempts per 5-minute window, guessing
///     is not a viable attack.</item>
/// </list>
///
/// Redeeming it yields a 32-byte per-device token, which is what actually authenticates calls.
/// </summary>
public sealed class PairingService
{
    /// <summary>Excludes characters that are misread when copied by eye: I, O, 0, 1.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 8;
    private const int MaxAttempts = 5;

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly PairedDeviceStore _devices;
    private readonly ILogger<PairingService> _logger;
    private readonly object _gate = new();

    private string? _activeCode;
    private DateTimeOffset _expiresAt;
    private int _attemptsRemaining;

    public PairingService(PairedDeviceStore devices, ILogger<PairingService> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    /// <summary>True while a code is live, which is the only time <see cref="TryRedeemAsync"/> can succeed.</summary>
    public bool IsPairingActive
    {
        get
        {
            lock (_gate)
            {
                ExpireIfStaleUnsafe();
                return _activeCode is not null;
            }
        }
    }

    public PairingCodeDto StartPairing()
    {
        lock (_gate)
        {
            _activeCode = GenerateCode();
            _expiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime);
            _attemptsRemaining = MaxAttempts;
            _logger.LogInformation("Pairing window opened; code expires at {ExpiresAt:o}.", _expiresAt);
            return new PairingCodeDto(_activeCode, _expiresAt, _attemptsRemaining);
        }
    }

    public void CancelPairing()
    {
        lock (_gate)
        {
            _activeCode = null;
            _attemptsRemaining = 0;
        }
    }

    public async Task<PairingRedeemResponse?> TryRedeemAsync(
        string code,
        string deviceName,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ExpireIfStaleUnsafe();

            if (_activeCode is null)
            {
                _logger.LogWarning("Pairing attempt rejected: no pairing window is open.");
                return null;
            }

            if (!FixedTimeEquals(code.Trim(), _activeCode))
            {
                _attemptsRemaining--;
                _logger.LogWarning(
                    "Pairing attempt rejected: wrong code. {AttemptsRemaining} attempts left.",
                    _attemptsRemaining);

                if (_attemptsRemaining <= 0)
                {
                    _activeCode = null;
                    _logger.LogWarning("Pairing window closed after too many wrong codes.");
                }

                return null;
            }

            // Single use: burn it before doing any async work so two racing redeems cannot both win.
            _activeCode = null;
        }

        var resolvedName = string.IsNullOrWhiteSpace(deviceName) ? "Unnamed device" : deviceName.Trim();
        var response = await _devices.AddAsync(resolvedName, cancellationToken);
        _logger.LogInformation("Paired device {DeviceName} ({DeviceId}).", response.DeviceName, response.DeviceId);
        return response;
    }

    private void ExpireIfStaleUnsafe()
    {
        if (_activeCode is not null && DateTimeOffset.UtcNow >= _expiresAt)
        {
            _activeCode = null;
            _logger.LogInformation("Pairing window expired.");
        }
    }

    private static string GenerateCode()
    {
        var builder = new StringBuilder(CodeLength);
        for (var index = 0; index < CodeLength; index++)
        {
            builder.Append(CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]);
        }

        return builder.ToString();
    }

    private static bool FixedTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToUpperInvariant())),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected.ToUpperInvariant())));
}
