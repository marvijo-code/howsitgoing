using HowsItGoing.Contracts;
using Microsoft.UI;
using Microsoft.UI.Xaml;
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

/// <summary>
/// Thread bubbles: the user's own turns read as a tinted panel on the right, the agent's on the
/// left, so a long transcript is scannable by shape before you read a word.
/// </summary>
public sealed class MessageRoleToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is SessionMessageRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Keeps chat bubbles off the far edge on both sides without hard-coding a phone width: the user's
/// turn is inset on the left, the agent's on the right, so the shape of the transcript is readable
/// at 390px and at 900px alike.
/// </summary>
public sealed class MessageRoleToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is SessionMessageRole.User
            ? new Thickness(34, 0, 0, 8)
            : new Thickness(0, 0, 34, 8);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class MessageRoleToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            SessionMessageRole.User => "You",
            SessionMessageRole.Tool => "Tool",
            _ => "Agent"
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class MessageRoleToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush UserBrush = new(Color.FromArgb(0xFF, 0xC4, 0xB5, 0xFD));
    private static readonly SolidColorBrush AgentBrush = new(Color.FromArgb(0xFF, 0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush ToolBrush = new(Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            SessionMessageRole.User => UserBrush,
            SessionMessageRole.Tool => ToolBrush,
            _ => AgentBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class MessageRoleToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush UserBrush = new(Color.FromArgb(0x2E, 0xA7, 0x8B, 0xFA));
    private static readonly SolidColorBrush AgentBrush = new(Color.FromArgb(0x1F, 0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush ToolBrush = new(Color.FromArgb(0x1F, 0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            SessionMessageRole.User => UserBrush,
            SessionMessageRole.Tool => ToolBrush,
            _ => AgentBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Pill filters: compares the bound filter value with the pill's own value in ConverterParameter
/// and paints the active pill. Keeps five pills on one row without five view-model properties.
/// </summary>
public sealed class FilterMatchToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(0x2E, 0xA7, 0x8B, 0xFA));
    private static readonly SolidColorBrush IdleBrush = new(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? ActiveBrush
            : IdleBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class FilterMatchToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(0xFF, 0xC4, 0xB5, 0xFD));
    private static readonly SolidColorBrush IdleBrush = new(Color.FromArgb(0xFF, 0x71, 0x71, 0x7A));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? ActiveBrush
            : IdleBrush;

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
