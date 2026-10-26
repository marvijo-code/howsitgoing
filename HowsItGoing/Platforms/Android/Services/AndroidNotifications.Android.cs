#pragma warning disable CA1416 // The whole file is Android-only; channel APIs are guarded by Build.VERSION checks.
using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using AndroidX.Core.App;
using HowsItGoing.Contracts;
using HowsItGoing.Models;
using HowsItGoing.Services;

namespace HowsItGoing.Droid;

/// <summary>
/// Builds Android notification channels from the user's sound/vibration/importance choices.
///
/// A channel's sound is immutable after creation, so the channel id carries a fingerprint of the
/// settings. When the user changes the sound, a new channel is created and the old ones are deleted;
/// that is the only way an installed app can actually change its alert sound.
/// </summary>
public static class AndroidNotifications
{
    public const string EventChannelPrefix = "howsitgoing-events-";
    public const string MonitorChannelId = "howsitgoing-monitor";
    public const string GroupKey = "howsitgoing-alerts";
    public const int ForegroundNotificationId = 1001;
    public const string SoundDefault = "default";
    public const string SoundNone = "none";

    private static readonly long[] VibrationPattern = [0, 250, 150, 250];

    /// <summary>
    /// Device notification tones plus the two always-present choices. Enumeration is best-effort:
    /// some OEM builds refuse the ringtone cursor, and a picker with Default/None still works.
    /// </summary>
    public static IReadOnlyList<NotificationSoundOption> GetSoundOptions(Context context)
    {
        var options = new List<NotificationSoundOption>
        {
            new(SoundDefault, "System default"),
            new(SoundNone, "Silent")
        };

        try
        {
            var manager = new RingtoneManager(context);
            manager.SetType(RingtoneType.Notification);
            var cursor = manager.Cursor;
            if (cursor is null)
            {
                return options;
            }

            using (cursor)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { SoundDefault, SoundNone };
                while (cursor.MoveToNext())
                {
                    var uri = manager.GetRingtoneUri(cursor.Position);
                    if (uri is null)
                    {
                        continue;
                    }

                    var key = uri.ToString();
                    if (key is null || !seen.Add(key))
                    {
                        continue;
                    }

                    var title = manager.GetRingtone(cursor.Position)?.GetTitle(context) ?? "Notification tone";
                    options.Add(new NotificationSoundOption(key, title));
                }
            }
        }
        catch
        {
            // Fall back to default/silent.
        }

        return options;
    }

    public static NotificationImportance ResolveImportance(string? importance) => importance switch
    {
        "High" => NotificationImportance.High,
        "Low" => NotificationImportance.Low,
        _ => NotificationImportance.Default
    };

    /// <summary>The channel this configuration must post to. The id changes with the sound.</summary>
    public static string GetEventChannelId(AppSettings settings)
    {
        var sound = string.IsNullOrWhiteSpace(settings.NotificationSoundUri)
            ? SoundDefault
            : settings.NotificationSoundUri.Trim();
        var fingerprint = HashCode.Combine(
            sound,
            settings.NotificationVibration,
            ResolveImportance(settings.NotificationImportance));

        return $"{EventChannelPrefix}{fingerprint:x8}";
    }

    public static void EnsureChannels(Context context, AppSettings settings)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager is null)
        {
            return;
        }

        var monitorChannel = new NotificationChannel(
            MonitorChannelId,
            "Bridge monitoring",
            NotificationImportance.Low)
        {
            Description = "Keeps the app connected to the local bridge."
        };
        monitorChannel.SetShowBadge(false);
        manager.CreateNotificationChannel(monitorChannel);

        var eventChannelId = GetEventChannelId(settings);
        var importance = ResolveImportance(settings.NotificationImportance);
        var eventChannel = new NotificationChannel(
            eventChannelId,
            "Agent and GitHub alerts",
            importance)
        {
            Description = "Session completions, failures, follow-ups and GitHub pushes."
        };

        var soundUri = ResolveSoundUri(settings.NotificationSoundUri);
        if (soundUri is null)
        {
            eventChannel.SetSound(null, null);
        }
        else
        {
            eventChannel.SetSound(
                soundUri,
                new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Notification)
                    .SetContentType(AudioContentType.Sonification)
                    .Build());
        }

        eventChannel.EnableVibration(settings.NotificationVibration);
        if (settings.NotificationVibration)
        {
            eventChannel.SetVibrationPattern(VibrationPattern);
        }

        eventChannel.EnableLights(true);
        eventChannel.SetShowBadge(true);
        manager.CreateNotificationChannel(eventChannel);

        // Retire channels built from an older sound/vibration choice so the settings screen in
        // Android does not fill with stale duplicates.
        foreach (var channel in manager.NotificationChannels ?? [])
        {
            if (channel.Id is { } id &&
                id.StartsWith(EventChannelPrefix, StringComparison.Ordinal) &&
                !string.Equals(id, eventChannelId, StringComparison.Ordinal))
            {
                manager.DeleteNotificationChannel(id);
            }
        }
    }

    /// <summary>Null means silent.</summary>
    public static Android.Net.Uri? ResolveSoundUri(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            string.Equals(configured, SoundNone, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(configured, SoundDefault, StringComparison.OrdinalIgnoreCase))
        {
            return RingtoneManager.GetDefaultUri(RingtoneType.Notification);
        }

        return Android.Net.Uri.Parse(configured);
    }

    public static Notification BuildForegroundNotification(Context context) =>
        new NotificationCompat.Builder(context, MonitorChannelId)
            .SetContentTitle("How's It Going")
            .SetContentText("Monitoring agent sessions and GitHub pushes.")
            .SetSmallIcon(Resource.Mipmap.icon)
            .SetOngoing(true)
            .SetPriority((int)NotificationPriority.Low)
            .Build();

    public static void Show(Context context, AppSettings settings, BridgeNotificationDto notification)
    {
        EnsureChannels(context, settings);

        var builder = new NotificationCompat.Builder(context, GetEventChannelId(settings))
            .SetSmallIcon(Resource.Mipmap.icon)
            .SetContentTitle(notification.Title)
            .SetContentText(notification.Message)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(notification.Message))
            .SetAutoCancel(true)
            .SetShowWhen(true)
            .SetWhen(notification.OccurredAt.ToUnixTimeMilliseconds())
            .SetGroup(GroupKey)
            .SetCategory(Notification.CategoryStatus)
            .SetPriority((int)ToCompatPriority(ResolveImportance(settings.NotificationImportance)));

        var contentIntent = CreateOpenAppIntent(context);
        if (contentIntent is not null)
        {
            builder.SetContentIntent(contentIntent);
        }

        // Pre-O devices have no channel, so the per-notification settings are the only ones that
        // apply there.
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            if (settings.NotificationVibration)
            {
                builder.SetVibrate(VibrationPattern);
            }

            var legacySound = ResolveSoundUri(settings.NotificationSoundUri);
            if (legacySound is not null)
            {
                builder.SetSound(legacySound);
            }
        }

        NotificationManagerCompat.From(context)
            .Notify(NotificationId(notification.Id), builder.Build());
    }

    /// <summary>A test alert must prove the chosen sound and vibration, not a convention.</summary>
    public static void ShowTest(Context context, AppSettings settings)
    {
        Show(
            context,
            settings,
            new BridgeNotificationDto(
                $"android-test:{Guid.NewGuid():N}",
                BridgeNotificationKind.AgentThreadStarted,
                "How's It Going",
                "Test alert - this is the sound and vibration your alerts will use.",
                DateTimeOffset.Now,
                null,
                null,
                null));
    }

    public static int NotificationId(string notificationId)
    {
        // String.GetHashCode is randomised per process, which is fine: the id only needs to keep
        // two live alerts from the same run apart.
        return notificationId.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF;
    }

    private static PendingIntent? CreateOpenAppIntent(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(MainActivity));
            intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            var flags = PendingIntentFlags.UpdateCurrent;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                flags |= PendingIntentFlags.Immutable;
            }

            return PendingIntent.GetActivity(context, 0, intent, flags);
        }
        catch
        {
            return null;
        }
    }

    private static NotificationPriority ToCompatPriority(NotificationImportance importance) => importance switch
    {
        NotificationImportance.High => NotificationPriority.High,
        NotificationImportance.Low => NotificationPriority.Low,
        _ => NotificationPriority.Default
    };
}
