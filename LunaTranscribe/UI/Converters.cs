using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LunaTranscribe.Engine;
using LunaTranscribe.Settings;
using LunaCore;

namespace LunaTranscribe.UI;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class LanguageNameConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is AppLanguage l ? l.DisplayName() : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EngineLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is EngineKind k ? k.Label() : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EngineShortLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is EngineKind k ? k.ShortLabel() : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
