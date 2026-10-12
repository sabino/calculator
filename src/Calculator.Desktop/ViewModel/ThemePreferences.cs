// Licensed under the MIT License.
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace CalculatorApp.Desktop;

internal static class ThemePreferences
{
    private const string ThemeKey = "SelectedAppTheme";

    public static ElementTheme LoadTheme()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        if (values.TryGetValue(ThemeKey, out var value) && value is string text
            && Enum.TryParse<ElementTheme>(text, out var theme) && Enum.IsDefined(theme)) return theme;
        return ElementTheme.Default;
    }

    public static void SaveTheme(ElementTheme theme) =>
        ApplicationData.Current.LocalSettings.Values[ThemeKey] = theme.ToString();
}
