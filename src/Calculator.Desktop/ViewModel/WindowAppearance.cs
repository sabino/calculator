// Licensed under the MIT License.
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;

namespace CalculatorApp.Desktop;

internal static partial class WindowAppearance
{
    // AppWindow.Resize includes Win32's frame, while the other hosts resize the
    // content. Keep the shared layout and minimums in content coordinates.
    public static void ResizeContent(Window window, SizeInt32 content)
    {
        var frame = FrameInsets(window);
        window.AppWindow.Resize(new SizeInt32 { Width = content.Width + frame.Width, Height = content.Height + frame.Height });
    }

    public static void ApplyMinimumContentSize(Window window)
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var frame = FrameInsets(window);
        presenter.PreferredMinimumWidth = 320 + frame.Width;
        presenter.PreferredMinimumHeight = 500 + frame.Height;
    }

    private static SizeInt32 FrameInsets(Window window)
    {
        if (!OperatingSystem.IsWindows()) return new SizeInt32();
        var handle = (WindowHelper.GetNativeWindow(window) as Win32NativeWindow)?.Hwnd ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var outer) || !GetClientRect(handle, out var client)) return new SizeInt32();
        return new SizeInt32 { Width = Math.Max(0, outer.Right - outer.Left - client.Right + client.Left),
            Height = Math.Max(0, outer.Bottom - outer.Top - client.Bottom + client.Top) };
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, uint size);
}
