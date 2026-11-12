using System.Runtime.InteropServices;
using Photino.NET;
using Serilog;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// Applies the Windows 11 Mica backdrop to the transparent Photino window and
/// supplies the native hit-testing and caption actions required by chromeless mode.
/// </summary>
internal static class WindowsMicaWindow
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcDestroy = 0x0082;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsMaximizeBox = 0x00010000;
    private const long WsSysMenu = 0x00080000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const uint SmCxSizeFrame = 32;
    private const uint SmCySizeFrame = 33;
    private const uint SmCxPaddedBorder = 92;
    private const uint LwaAlpha = 0x00000002;

    private static readonly UIntPtr SubclassId = new(0x54414749);
    private static readonly SubclassProcedure SubclassCallback = WindowProcedure;

    private static IntPtr _windowHandle;

    public static bool IsActive { get; private set; }

    public static void Initialize(PhotinoWindow window)
    {
        if (!PhotinoWindow.IsWindowsPlatform
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return;
        }

        _windowHandle = window.WindowHandle;

        // Photino creates chromeless windows with WS_POPUP, which has no resize frame.
        // Add the native sizing/system-menu styles while keeping the window captionless.
        long windowStyle = GetWindowLongPtr(_windowHandle, GwlStyle).ToInt64();
        windowStyle |= WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu;
        _ = SetWindowLongPtr(_windowHandle, GwlStyle, new IntPtr(windowStyle));
        _ = SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);

        int backdropType = DwmsbtMainWindow;
        int result = DwmSetWindowAttribute(
            _windowHandle,
            DwmwaSystemBackdropType,
            ref backdropType,
            sizeof(int));

        IsActive = result >= 0;
        if (!IsActive)
        {
            Log.Warning(
                "Windows Mica could not be enabled. DwmSetWindowAttribute returned HRESULT 0x{HResult:X8}.",
                result);

            // Photino's transparent window is a layered window. If DWM cannot provide
            // the requested backdrop, make the whole window opaque rather than leaving
            // transparent regions that reveal the desktop.
            _ = SetLayeredWindowAttributes(_windowHandle, 0, byte.MaxValue, LwaAlpha);
        }

        if (!SetWindowSubclass(_windowHandle, SubclassCallback, SubclassId, UIntPtr.Zero))
        {
            Log.Warning(
                "Could not install the native window resize handler. Win32 error: {Error}.",
                Marshal.GetLastWin32Error());
        }
    }

    public static void HandleWebMessage(PhotinoWindow window, string message)
    {
        switch (message)
        {
            case "tagium:window:drag":
                ReleaseCapture();
                _ = SendMessage(_windowHandle, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
                break;
            case "tagium:window:minimize":
                window.Minimized = true;
                break;
            case "tagium:window:maximize":
                window.Maximized = !window.Maximized;
                break;
            case "tagium:window:close":
                window.Close();
                break;
        }
    }

    private static IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == WmNcHitTest && !IsZoomed(windowHandle)
            && GetWindowRect(windowHandle, out NativeRect bounds))
        {
            long packedCoordinates = lParam.ToInt64();
            int x = unchecked((short)(packedCoordinates & 0xFFFF));
            int y = unchecked((short)((packedCoordinates >> 16) & 0xFFFF));

            uint dpi = GetDpiForWindow(windowHandle);
            int borderX = GetSystemMetricsForDpi(SmCxSizeFrame, dpi)
                + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
            int borderY = GetSystemMetricsForDpi(SmCySizeFrame, dpi)
                + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);

            bool left = x < bounds.Left + borderX;
            bool right = x >= bounds.Right - borderX;
            bool top = y < bounds.Top + borderY;
            bool bottom = y >= bounds.Bottom - borderY;

            if (top && left) return new IntPtr(HtTopLeft);
            if (top && right) return new IntPtr(HtTopRight);
            if (bottom && left) return new IntPtr(HtBottomLeft);
            if (bottom && right) return new IntPtr(HtBottomRight);
            if (left) return new IntPtr(HtLeft);
            if (right) return new IntPtr(HtRight);
            if (top) return new IntPtr(HtTop);
            if (bottom) return new IntPtr(HtBottom);
        }

        IntPtr result = DefSubclassProc(windowHandle, message, wParam, lParam);

        if (message == WmNcDestroy)
        {
            _ = RemoveWindowSubclass(windowHandle, SubclassCallback, SubclassId);
            _windowHandle = IntPtr.Zero;
            IsActive = false;
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure callback,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        SubclassProcedure callback,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(uint index, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr windowHandle,
        uint colorKey,
        byte alpha,
        uint flags);
}
