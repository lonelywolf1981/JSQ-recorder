using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using JSQ.Core.Models;

namespace JSQ.UI.WPF.Converters;

/// <summary>
/// Многозначный конвертер для фона строки канала.
/// Входные значения (в порядке MultiBinding):
///   [0] RowHighlightColor — string? (hex-цвет или null)
///   [1] Status            — HealthStatus
///   [2] IsRecording       — bool
///   [3] AlternationIndex  — int (0 или 1, от ItemsControl)
///
/// Приоритет (от высшего к низшему):
///   1. Alarm   → красный
///   2. Warning → жёлтый
///   3. Пользовательский цвет (если задан)
///   4. OK + IsRecording → зелёный
///   5. AlternationIndex == 1 → светло-серый (зебра)
///   6. White   → по умолчанию
/// </summary>
public sealed class RowBackgroundConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush AlarmBrush     = Freeze(0xFF, 0xCD, 0xD2); // #FFCDD2
    private static readonly SolidColorBrush WarningBrush   = Freeze(0xFF, 0xF9, 0xC4); // #FFF9C4
    private static readonly SolidColorBrush RecordingBrush = Freeze(0xF1, 0xF8, 0xF1); // #F1F8F1
    private static readonly SolidColorBrush ZebraBrush     = Freeze(0xF5, 0xF5, 0xF5); // #F5F5F5
    private static readonly SolidColorBrush DefaultBrush   = Brushes.White;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var highlightColor   = values.Length > 0 ? values[0] as string           : null;
        var status           = values.Length > 1 && values[1] is HealthStatus s  ? s    : HealthStatus.NoData;
        var isRecording      = values.Length > 2 && values[2] is bool b          && b;
        var alternationIndex = values.Length > 3 && values[3] is int ai          ? ai   : 0;

        // 1. Статусы Alarm / Warning — всегда видны независимо от пометок
        if (status == HealthStatus.Alarm)   return AlarmBrush;
        if (status == HealthStatus.Warning) return WarningBrush;

        // 2. Пользовательский цвет пометки
        if (!string.IsNullOrEmpty(highlightColor))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(highlightColor);
                return new SolidColorBrush(color);
            }
            catch { }
        }

        // 3. Идёт запись (OK + IsRecording)
        if (isRecording && status == HealthStatus.OK)
            return RecordingBrush;

        // 4. Зебра (нечётные строки)
        if (alternationIndex == 1)
            return ZebraBrush;

        return DefaultBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
