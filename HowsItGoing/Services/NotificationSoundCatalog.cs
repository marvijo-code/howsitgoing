namespace HowsItGoing.Services;

/// <summary>One entry in the Android notification sound picker. Shared so the view model can bind
/// it on every head; only Android fills it with device tones.</summary>
public sealed partial record NotificationSoundOption(string Key, string Label);

public static class NotificationSoundCatalog
{
    public const string DefaultKey = "default";
    public const string NoneKey = "none";

    /// <summary>The two choices that exist on every head, without touching the media provider.</summary>
    public static IReadOnlyList<NotificationSoundOption> FixedOptions() =>
    [
        new(DefaultKey, "System default"),
        new(NoneKey, "Silent")
    ];

    /// <summary>
    /// Device notification tones where the platform can enumerate them. Enumeration queries the
    /// media provider, which takes seconds on some phones, so callers must run this OFF the UI
    /// thread. Every other head (and any Android build that refuses the cursor) gets the two
    /// choices that always work.
    /// </summary>
    public static IReadOnlyList<NotificationSoundOption> Load()
    {
#if ANDROID
        try
        {
            return HowsItGoing.Droid.AndroidNotifications.GetSoundOptions(Android.App.Application.Context);
        }
        catch
        {
            // Fall through to the fixed choices.
        }
#endif
        return FixedOptions();
    }
}
