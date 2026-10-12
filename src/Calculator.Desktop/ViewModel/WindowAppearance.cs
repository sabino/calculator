// Licensed under the MIT License.
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;

namespace CalculatorApp.Desktop;

internal static partial class WindowAppearance
{
    public static bool ApplyTheme(Window window, ElementTheme theme, Color background)
    {
        if (!OperatingSystem.IsWindows()) return true;
        // Uno's WindowNative API exposes its logical window id. DWM needs the host's HWND.
        var handle = (WindowHelper.GetNativeWindow(window) as Win32NativeWindow)?.Hwnd ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;

        // The native host follows the system theme; Calculator also supports an explicit app theme.
        uint dark = theme == ElementTheme.Dark ? 1u : 0u;
        int darkResult = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
        uint caption = (uint)(background.R | background.G << 8 | background.B << 16);
        int captionResult = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint)) : 0;
        if (darkResult < 0 || captionResult < 0)
            Console.Error.WriteLine($"Window theme: handle={handle}, dark={dark}, caption={caption:X6}, HRESULT={darkResult:X8}/{captionResult:X8}");
        return darkResult >= 0 && captionResult >= 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, uint size);
}
