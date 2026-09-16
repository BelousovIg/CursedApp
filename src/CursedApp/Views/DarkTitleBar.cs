using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CursedApp.Views;

/// <summary>
/// Paints the native title bar to match the app's dark theme.
///
/// This uses DWM window attributes rather than a custom WindowChrome on purpose:
/// the caption keeps the real system buttons, Snap Layouts, double-click to
/// maximise and the drag behaviour users expect, and there is nothing to
/// reimplement. Attributes that the running Windows build does not know are
/// simply ignored.
/// </summary>
internal static class DarkTitleBar
{
    // Documented DWMWINDOWATTRIBUTE values.
    private const int UseImmersiveDarkMode = 20;   // Windows 10 1809+
    private const int BorderColor = 34;            // Windows 11 build 22000+
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    /// <summary>Tells DWM to leave an attribute alone.</summary>
    private const uint ColorDefault = 0xFFFFFFFF;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies the dark caption once the window has an HWND. Safe to call on any
    /// Windows version: unsupported attributes fail silently.
    /// </summary>
    public static void Apply(Window window, Color caption, Color text, Color border)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            Apply(source.Handle, caption, text, border);
            return;
        }

        // Called before the handle exists; wait for it.
        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Apply(new WindowInteropHelper(window).Handle, caption, text, border);
        }
    }

    private static void Apply(IntPtr hwnd, Color caption, Color text, Color border)
    {
        if (hwnd == IntPtr.Zero)
            return;

        // Dark mode first: it drives the button glyph colours and the hover
        // highlights, which the colour attributes below do not cover.
        Set(hwnd, UseImmersiveDarkMode, 1);

        Set(hwnd, CaptionColor, ToColorRef(caption));
        Set(hwnd, TextColor, ToColorRef(text));
        Set(hwnd, BorderColor, ToColorRef(border));
    }

    private static void Set(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No dwmapi.dll: nothing to theme.
        }
        catch (EntryPointNotFoundException)
        {
            // Older shell without this export.
        }
    }

    /// <summary>DWM colour attributes take a COLORREF, which is 0x00BBGGRR.</summary>
    private static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    /// <summary>Restores the system default caption colours.</summary>
    public static void Reset(IntPtr hwnd)
    {
        Set(hwnd, CaptionColor, unchecked((int)ColorDefault));
        Set(hwnd, TextColor, unchecked((int)ColorDefault));
        Set(hwnd, BorderColor, unchecked((int)ColorDefault));
    }
}
