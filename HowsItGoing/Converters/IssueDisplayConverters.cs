using System.Collections;
using HowsItGoing.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HowsItGoing.Converters;

public sealed class IssueStateToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is IssueState.Closed ? "CLOSED" : "OPEN";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class IssueStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OpenBrush = new(Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush ClosedBrush = new(Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is IssueState.Closed ? ClosedBrush : OpenBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class IssueStateToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OpenBrush = new(Color.FromArgb(0x2E, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush ClosedBrush = new(Color.FromArgb(0x2E, 0xA7, 0x8B, 0xFA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is IssueState.Closed ? ClosedBrush : OpenBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class PullRequestStateToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            PullRequestState.Merged => "MERGED",
            PullRequestState.Closed => "CLOSED",
            _ => "OPEN"
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class PullRequestStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OpenBrush = new(Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush MergedBrush = new(Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA));
    private static readonly SolidColorBrush ClosedBrush = new(Color.FromArgb(0xFF, 0xF8, 0x71, 0x71));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            PullRequestState.Merged => MergedBrush,
            PullRequestState.Closed => ClosedBrush,
            _ => OpenBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class PullRequestStateToBackgroundBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OpenBrush = new(Color.FromArgb(0x2E, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush MergedBrush = new(Color.FromArgb(0x2E, 0xA7, 0x8B, 0xFA));
    private static readonly SolidColorBrush ClosedBrush = new(Color.FromArgb(0x2E, 0xF8, 0x71, 0x71));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            PullRequestState.Merged => MergedBrush,
            PullRequestState.Closed => ClosedBrush,
            _ => OpenBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class CheckStatusToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            CheckRollupStatus.Success => "checks passed",
            CheckRollupStatus.Failure => "checks failed",
            CheckRollupStatus.Pending => "checks running",
            _ => string.Empty
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class CheckStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush FailureBrush = new(Color.FromArgb(0xFF, 0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush PendingBrush = new(Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush NoneBrush = new(Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            CheckRollupStatus.Success => SuccessBrush,
            CheckRollupStatus.Failure => FailureBrush,
            CheckRollupStatus.Pending => PendingBrush,
            _ => NoneBrush
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class CheckStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is CheckRollupStatus and not CheckRollupStatus.None ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Non-empty collection -> Visible, otherwise Collapsed. Pass parameter "invert" to flip.</summary>
public sealed class CollectionToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasItems = value is IEnumerable enumerable && enumerable.Cast<object>().Any();
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return hasItems != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Formats an issue/PR number as "#123".</summary>
public sealed class NumberToHashConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int number ? $"#{number}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Formats a list of issue/PR numbers as "#299 #298".</summary>
public sealed class NumberListToHashesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is IEnumerable<int> numbers ? string.Join("  ", numbers.Select(n => $"#{n}")) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Joins a list of strings with ", " (used for labels and assignees). Optional parameter is a prefix.</summary>
public sealed class StringListToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not IEnumerable<string> items)
        {
            return string.Empty;
        }

        var joined = string.Join(", ", items.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrEmpty(joined))
        {
            return string.Empty;
        }

        return parameter is string prefix && !string.IsNullOrEmpty(prefix) ? $"{prefix}{joined}" : joined;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BranchToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string branch && !string.IsNullOrWhiteSpace(branch) ? branch : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
