using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HowsItGoing.Bridge.Push;
using HowsItGoing.Contracts;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

[TestFixture]
public sealed class WebPushCryptoTests
{
    // RFC 8291 section 5. The salt and the application-server key pair are fixed there, which is the
    // only reason a hand-rolled aes128gcm implementation is defensible: the output is checkable.
    private const string Plaintext = "When I grow up, I want to be a watermelon";
    private const string UserAgentPublicKey = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string ServerPublicKey = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string ServerPrivateKey = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string ExpectedBody =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6" +
        "TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    [Test]
    public void Encrypt_reproduces_the_rfc8291_test_vector()
    {
        using var serverKey = WebPushCrypto.CreateServerKey(
            Base64Url.DecodeFromChars(ServerPublicKey),
            Base64Url.DecodeFromChars(ServerPrivateKey));

        var body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(Plaintext),
            Base64Url.DecodeFromChars(UserAgentPublicKey),
            Base64Url.DecodeFromChars(AuthSecret),
            Base64Url.DecodeFromChars(Salt),
            serverKey);

        Base64Url.EncodeToString(body).Should().Be(ExpectedBody);
    }

    [Test]
    public void Encrypt_rejects_a_payload_too_large_for_one_record()
    {
        using var serverKey = WebPushCrypto.CreateEphemeralServerKey();

        FluentActions.Invoking(() => WebPushCrypto.Encrypt(
                new byte[4080],
                Base64Url.DecodeFromChars(UserAgentPublicKey),
                Base64Url.DecodeFromChars(AuthSecret),
                Base64Url.DecodeFromChars(Salt),
                serverKey))
            .Should().Throw<ArgumentException>();
    }

    [Test]
    public void Encrypt_with_a_generated_key_still_produces_a_well_formed_body()
    {
        using var serverKey = WebPushCrypto.CreateEphemeralServerKey();
        var salt = new byte[WebPushCrypto.SaltLength];

        var body = WebPushCrypto.Encrypt(
            "hello"u8,
            Base64Url.DecodeFromChars(UserAgentPublicKey),
            Base64Url.DecodeFromChars(AuthSecret),
            salt,
            serverKey);

        // salt(16) | record size(4) | key id length(1) | key id(65) | ciphertext("hello" + delimiter + tag)
        body.Length.Should().Be(16 + 4 + 1 + 65 + 6 + 16);
        body[20].Should().Be(65);
    }

    [Test]
    public void CreateVapidAuthorization_signs_a_token_the_push_service_can_verify()
    {
        var keys = WebPushCrypto.GenerateVapidKeys();

        var header = WebPushCrypto.CreateVapidAuthorization(
            new Uri("https://fcm.googleapis.com/fcm/send/abc123"),
            "mailto:someone@example.com",
            keys,
            DateTimeOffset.UnixEpoch.AddSeconds(1_800_000_000));

        header.Should().StartWith("vapid t=");
        header.Should().Contain($", k={keys.PublicKey}");

        var token = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');
        parts.Should().HaveCount(3);

        using var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        // The audience is the origin only - a path would tie the token to a single subscription.
        claims.RootElement.GetProperty("aud").GetString().Should().Be("https://fcm.googleapis.com");
        claims.RootElement.GetProperty("sub").GetString().Should().Be("mailto:someone@example.com");
        claims.RootElement.GetProperty("exp").GetInt64().Should().Be(1_800_000_000);
    }

    [Test]
    public void GenerateVapidKeys_produces_a_key_the_browser_will_accept()
    {
        var keys = WebPushCrypto.GenerateVapidKeys();

        var publicKey = Base64Url.DecodeFromChars(keys.PublicKey);
        publicKey.Should().HaveCount(65);
        publicKey[0].Should().Be(0x04);
        Base64Url.DecodeFromChars(keys.PrivateKey).Should().HaveCount(32);
        keys.PublicKey.Should().NotContain("=");
    }
}

[TestFixture]
public sealed class WebPushPreferenceEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 14, 0, 0, TimeSpan.Zero);

    private static BridgeNotificationDto Notification(
        BridgeNotificationKind kind = BridgeNotificationKind.CodexThreadCompleted,
        string title = "Ship the push feature",
        string message = "Codex marked the thread as complete.",
        string? repository = "marvijo-code/howsitgoing") =>
        new("n1", kind, title, message, Now, null, repository, null);

    [Test]
    public void A_default_subscription_receives_everything()
    {
        WebPushPreferenceEvaluator.Evaluate(new WebPushPreferencesDto(), Notification(), null, Now)
            .Should().Be(PushDecision.Deliver);
    }

    [Test]
    public void Disabling_a_subscription_silences_it_without_unregistering_it()
    {
        WebPushPreferenceEvaluator.Evaluate(new WebPushPreferencesDto(Enabled: false), Notification(), null, Now)
            .Should().Be(PushDecision.SubscriptionDisabled);
    }

    [Test]
    public void Kind_filters_keep_only_the_selected_kinds()
    {
        var preferences = new WebPushPreferencesDto(Kinds: [BridgeNotificationKind.GitHubPush]);

        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(BridgeNotificationKind.GitHubPush), null, Now)
            .Should().Be(PushDecision.Deliver);
        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(BridgeNotificationKind.FollowUpFailed), null, Now)
            .Should().Be(PushDecision.KindFiltered);
    }

    [Test]
    public void Repository_filters_match_a_slug_and_a_working_directory_alike()
    {
        var preferences = new WebPushPreferencesDto(Repositories: ["howsitgoing"]);

        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(repository: "marvijo-code/howsitgoing"), null, Now)
            .Should().Be(PushDecision.Deliver);
        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(repository: @"C:\dev\howsitgoing"), null, Now)
            .Should().Be(PushDecision.Deliver);
        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(repository: "marvijo-code/learn-racer"), null, Now)
            .Should().Be(PushDecision.RepositoryFiltered);
    }

    [Test]
    public void A_repository_filter_drops_notifications_that_name_no_repository()
    {
        WebPushPreferenceEvaluator.Evaluate(
                new WebPushPreferencesDto(Repositories: ["howsitgoing"]),
                Notification(repository: null),
                null,
                Now)
            .Should().Be(PushDecision.RepositoryFiltered);
    }

    [Test]
    public void Keyword_filters_look_at_both_the_title_and_the_message()
    {
        var preferences = new WebPushPreferencesDto(Keyword: "complete");

        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(), null, Now)
            .Should().Be(PushDecision.Deliver);
        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(title: "Build failed", message: "exit code 1"), null, Now)
            .Should().Be(PushDecision.KeywordFiltered);
    }

    [Test]
    public void Throttling_holds_back_a_second_push_inside_the_minimum_interval()
    {
        var preferences = new WebPushPreferencesDto(MinIntervalSeconds: 300);

        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(), Now.AddSeconds(-60), Now)
            .Should().Be(PushDecision.Throttled);
        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(), Now.AddSeconds(-301), Now)
            .Should().Be(PushDecision.Deliver);
        WebPushPreferenceEvaluator.Evaluate(preferences, Notification(), null, Now)
            .Should().Be(PushDecision.Deliver);
    }

    [Test]
    public void Quiet_hours_are_evaluated_in_the_subscriber_local_time()
    {
        // 14:00 UTC is 23:00 in UTC+9, inside a 22:00-07:00 window, but 06:00 in UTC-8, which is not.
        var overnight = new WebPushPreferencesDto(QuietHoursStart: 22, QuietHoursEnd: 7, UtcOffsetMinutes: 9 * 60);
        WebPushPreferenceEvaluator.Evaluate(overnight, Notification(), null, Now)
            .Should().Be(PushDecision.QuietHours);

        var daytime = overnight with { UtcOffsetMinutes = -8 * 60 };
        WebPushPreferenceEvaluator.Evaluate(daytime, Notification(), null, Now)
            .Should().Be(PushDecision.QuietHours);

        var afternoon = overnight with { UtcOffsetMinutes = 0 };
        WebPushPreferenceEvaluator.Evaluate(afternoon, Notification(), null, Now)
            .Should().Be(PushDecision.Deliver);
    }

    [Test]
    public void A_same_hour_window_is_treated_as_no_quiet_hours_rather_than_permanent_silence()
    {
        WebPushPreferenceEvaluator.IsWithinQuietHours(
                new WebPushPreferencesDto(QuietHoursStart: 9, QuietHoursEnd: 9),
                Now)
            .Should().BeFalse();
    }

    [Test]
    public void An_impossible_utc_offset_is_clamped_instead_of_throwing()
    {
        FluentActions.Invoking(() => WebPushPreferenceEvaluator.Evaluate(
                new WebPushPreferencesDto(QuietHoursStart: 1, QuietHoursEnd: 2, UtcOffsetMinutes: 99_999),
                Notification(),
                null,
                Now))
            .Should().NotThrow();
    }
}
