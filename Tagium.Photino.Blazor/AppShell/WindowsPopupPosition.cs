using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using Photino.NET;

namespace Tagium.Photino.Blazor.AppShell;

internal static class WindowsPopupPosition
{
    private const uint MonitorDefaultToNearest = 2;

    public static Point GetViewOptionsLocation(
        PhotinoWindow owner,
        ViewOptionsAnchor anchor,
        int width,
        int height)
    {
        double scale = double.IsFinite(anchor.Scale) && anchor.Scale > 0
            ? anchor.Scale
            : 1;

        IntPtr ownerHandle = owner.WindowHandle;
        NativePoint anchorBottomRight = ToScreen(ownerHandle, anchor.Right, anchor.Bottom, scale);
        NativePoint anchorTopLeft = ToScreen(ownerHandle, anchor.Left, anchor.Top, scale);

        int physicalWidth = (int)Math.Ceiling(width * scale);
        int physicalHeight = (int)Math.Ceiling(height * scale);
        int left = anchorBottomRight.X - physicalWidth;
        int top = anchorBottomRight.Y;

        IntPtr monitor = MonitorFromPoint(anchorBottomRight, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return new Point(left, top);
        }

        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return new Point(left, top);
        }

        NativeRect workArea = monitorInfo.WorkArea;
        if (top + physicalHeight > workArea.Bottom)
        {
            top = anchorTopLeft.Y - physicalHeight;
        }

        left = Clamp(left, workArea.Left, workArea.Right - physicalWidth);
        top = Clamp(top, workArea.Top, workArea.Bottom - physicalHeight);

        return new Point(left, top);
    }

    private static NativePoint ToScreen(
        IntPtr windowHandle,
        double x,
        double y,
        double scale)
    {
        NativePoint point = new()
        {
            X = (int)Math.Round(x * scale),
            Y = (int)Math.Round(y * scale)
        };

        if (!ClientToScreen(windowHandle, ref point))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return point;
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        if (maximum < minimum)
        {
            return minimum;
        }

        return Math.Max(minimum, Math.Min(value, maximum));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
