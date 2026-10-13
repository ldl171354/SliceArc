using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SliceArc.App.Views;

/// <summary>
/// Turns the native title bar dark on Win10 20H1+ / Win11 when a dark palette
/// is active, so the OS chrome follows the app theme instead of staying white.
/// Older systems simply ignore the attribute (best-effort by design).
/// </summary>
public static class WindowTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>True for the palettes whose window chrome should be dark.</summary>
    public static bool ShouldBeDark => App.CurrentTheme is "Dark" or "Bandizip";

    /// <summary>Call once the window handle exists (SourceInitialized).</summary>
    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = ShouldBeDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch
        {
            // pre-20H1 Windows or dwmapi unavailable - keep the light chrome
        }
    }
}
