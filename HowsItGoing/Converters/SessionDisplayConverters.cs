using HowsItGoing.Contracts;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HowsItGoing.Converters;

public sealed class AgentToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        AgentKinds.DisplayName(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class AgentToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CodexBrush = new(Color.FromArgb(0xFF, 0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush ClaudeBrush = new(Color.FromArgb(0xFF, 0xE8, 0x8A, 0x66));
    private static readonly SolidColorBrush OpenCodeBrush = new(Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        AgentKinds.Normalize(value as string) switch
        {
            AgentKinds.ClaudeCode => ClaudeBrush,
            AgentKinds.OpenCode => OpenCodeBrush,
            _ => CodexBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class AgentToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CodexBrush = new(Color.FromArgb(0x2E, 0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush ClaudeBrush = new(Color.FromArgb(0x2E, 0xE8, 0x8A, 0x66));
    private static readonly SolidColorBrush OpenCodeBrush = new(Color.FromArgb(0x2E, 0x60, 0xA5, 0xFA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        AgentKinds.Normalize(value as string) switch
        {
            AgentKinds.ClaudeCode => ClaudeBrush,
            AgentKinds.OpenCode => OpenCodeBrush,
            _ => CodexBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush CompletedBrush = new(Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA));
    private static readonly SolidColorBrush IdleBrush = new(Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA));
    private static readonly SolidColorBrush ArchivedBrush = new(Color.FromArgb(0xFF, 0x71, 0x71, 0x7A));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            CodexSessionStatus.Running => RunningBrush,
            CodexSessionStatus.Completed => CompletedBrush,
            CodexSessionStatus.Archived => ArchivedBrush,
            _ => IdleBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StatusToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromArgb(0x2E, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush CompletedBrush = new(Color.FromArgb(0x2E, 0xA7, 0x8B, 0xFA));
    private static readonly SolidColorBrush IdleBrush = new(Color.FromArgb(0x24, 0xA1, 0xA1, 0xAA));
    private static readonly SolidColorBrush ArchivedBrush = new(Color.FromArgb(0x24, 0x71, 0x71, 0x7A));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            CodexSessionStatus.Running => RunningBrush,
            CodexSessionStatus.Completed => CompletedBrush,
            CodexSessionStatus.Archived => ArchivedBrush,
            _ => IdleBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class AbsoluteTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DateTimeOffset timestamp
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset timestamp)
        {
            return string.Empty;
        }

        var elapsed = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h ago",
            { TotalDays: < 7 } => $"{(int)elapsed.TotalDays}d ago",
            _ => timestamp.ToLocalTime().ToString("yyyy-MM-dd")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
