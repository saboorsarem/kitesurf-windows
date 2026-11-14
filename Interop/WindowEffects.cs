// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kitesurf.Windows.Interop;

/// <summary>Windows 11 chrome details: rounded corners, dark frame, border matched to the theme.</summary>
internal static partial class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        TrySet(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

        var corners = DwmwcpRound;
        TrySet(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));

        // COLORREF is 0x00BBGGRR; keep the frame in the same navy as the window chrome.
        var border = 0x0030_221A;
        TrySet(handle, DwmwaBorderColor, ref border, sizeof(int));
    }

    private static void TrySet(IntPtr handle, int attribute, ref int value, int size)
    {
        try
        {
            _ = DwmSetWindowAttribute(handle, attribute, ref value, size);
        }
        catch (DllNotFoundException)
        {
            // Windows 10 without the newer DWM attributes.
        }
        catch (EntryPointNotFoundException)
        {
            // Same.
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>The monitor work area under this window, in layout units.</summary>
    public static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;

        var dpi = VisualTreeHelper.GetDpi(window);
        var work = info.Work;
        return new Rect(
            work.Left / dpi.DpiScaleX,
            work.Top / dpi.DpiScaleY,
            (work.Right - work.Left) / dpi.DpiScaleX,
            (work.Bottom - work.Top) / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
