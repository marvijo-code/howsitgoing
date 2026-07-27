using FluentAssertions;
using HowsItGoing.Bridge.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

/// <summary>
/// An 8-character code is small enough that its safety comes entirely from the surrounding rules:
/// no window means no redeem, one success burns it, and a few wrong guesses burn it. These tests
/// pin those rules.
/// </summary>
[TestFixture]
public sealed class PairingTests
{
    private string _appDataOverride = string.Empty;
    private PairingService _pairing = null!;
    private PairedDeviceStore _devices = null!;

    [SetUp]
    public void SetUp()
    {
        // Point the store at a temp root so tests never touch the developer's real paired devices.
        _appDataOverride = Path.Combine(Path.GetTempPath(), "hig-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appDataOverride);

        _devices = new PairedDeviceStore(_appDataOverride);
        _pairing = new PairingService(_devices, NullLogger<PairingService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_appDataOverride))
            {
                Directory.Delete(_appDataOverride, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover temp state must not fail the run.
        }
    }

    [Test]
    public async Task Redeem_is_rejected_when_no_pairing_window_is_open()
    {
        _pairing.IsPairingActive.Should().BeFalse();

        var result = await _pairing.TryRedeemAsync("ABCD2345", "attacker", CancellationToken.None);

        result.Should().BeNull();
    }

    [Test]
    public void Generated_code_avoids_visually_ambiguous_characters()
    {
        var code = _pairing.StartPairing().Code;

        code.Should().HaveLength(8);
        code.Should().MatchRegex("^[A-HJ-NP-Z2-9]+$", "0/O/1/I are misread when copied by eye");
    }

    [Test]
    public async Task A_code_can_only_be_redeemed_once()
    {
        var code = _pairing.StartPairing().Code;

        var first = await _pairing.TryRedeemAsync(code, "Phone", CancellationToken.None);
        var second = await _pairing.TryRedeemAsync(code, "Impostor", CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull();
        _pairing.IsPairingActive.Should().BeFalse();
    }

    [Test]
    public async Task Wrong_codes_burn_the_window_so_the_short_code_cannot_be_brute_forced()
    {
        var code = _pairing.StartPairing().Code;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            (await _pairing.TryRedeemAsync("ZZZZZZZZ", "guesser", CancellationToken.None)).Should().BeNull();
        }

        _pairing.IsPairingActive.Should().BeFalse();

        // Even the real code no longer works: the window is gone.
        (await _pairing.TryRedeemAsync(code, "Phone", CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task Redeeming_issues_a_long_token_that_authenticates_and_can_be_revoked()
    {
        var code = _pairing.StartPairing().Code;
        var paired = await _pairing.TryRedeemAsync(code, "Marvin's Pixel", CancellationToken.None);

        paired.Should().NotBeNull();
        // 32 random bytes, base64: far beyond guessing, unlike the pairing code itself.
        paired!.AccessToken.Length.Should().BeGreaterThanOrEqualTo(40);
        paired.DeviceName.Should().Be("Marvin's Pixel");

        (await _devices.TryAuthenticateAsync(paired.AccessToken, CancellationToken.None)).Should().NotBeNull();
        (await _devices.TryAuthenticateAsync("not-the-token", CancellationToken.None)).Should().BeNull();

        (await _devices.RevokeAsync(paired.DeviceId, CancellationToken.None)).Should().BeTrue();
        (await _devices.TryAuthenticateAsync(paired.AccessToken, CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task Only_a_hash_of_the_token_is_persisted()
    {
        var code = _pairing.StartPairing().Code;
        var paired = await _pairing.TryRedeemAsync(code, "Phone", CancellationToken.None);

        var storeFile = Path.Combine(_appDataOverride, "HowsItGoing", "paired-devices.json");
        File.Exists(storeFile).Should().BeTrue();

        var contents = await File.ReadAllTextAsync(storeFile);
        contents.Should().NotContain(paired!.AccessToken, "a leaked store file must not be usable as a credential");
        contents.Should().Contain(PairedDeviceStore.HashToken(paired.AccessToken));
    }

    [Test]
    public async Task Codes_are_matched_case_insensitively_so_typing_is_forgiving()
    {
        var code = _pairing.StartPairing().Code;

        var paired = await _pairing.TryRedeemAsync(code.ToLowerInvariant(), "Phone", CancellationToken.None);

        paired.Should().NotBeNull();
    }

    [Test]
    public void Cancelling_closes_the_window()
    {
        _pairing.StartPairing();
        _pairing.IsPairingActive.Should().BeTrue();

        _pairing.CancelPairing();

        _pairing.IsPairingActive.Should().BeFalse();
    }
}
