using System.Runtime.InteropServices;
using Photino.NET;
using Serilog;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// Applies the Windows 11 system backdrop while preserving the native decorated window frame.
/// </summary>
internal static class WindowsMicaWindow
{
    private const int GwlExStyle = -20;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static bool IsActive { get; private set; }

    public static void Initialize(PhotinoWindow window)
    {
        if (!PhotinoWindow.IsWindowsPlatform
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return;
        }

        IntPtr windowHandle = window.WindowHandle;
        if (!EnableNoRedirectionBitmap(windowHandle))
        {
            return;
        }

        int backdropType = DwmsbtMainWindow;
        int result = DwmSetWindowAttribute(
            windowHandle,
            DwmwaSystemBackdropType,
            ref backdropType,
            sizeof(int));

        IsActive = result >= 0;
        if (!IsActive)
        {
            Log.Warning(
                "Windows Mica could not be enabled. DwmSetWindowAttribute returned HRESULT 0x{HResult:X8}.",
                result);
        }
    }

    private static bool EnableNoRedirectionBitmap(IntPtr windowHandle)
    {
        int extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt32();
        if ((extendedStyle & WsExNoRedirectionBitmap) != 0)
        {
            return true;
        }

        SetLastError(0);
        IntPtr previousStyle = SetWindowLongPtr(
            windowHandle,
            GwlExStyle,
            new IntPtr(extendedStyle | WsExNoRedirectionBitmap));
        int error = Marshal.GetLastWin32Error();

        if (previousStyle == IntPtr.Zero && error != 0)
        {
            Log.Warning(
                "Windows Mica could not enable WS_EX_NOREDIRECTIONBITMAP. SetWindowLongPtr failed with Win32 error {Error}.",
                error);
            return false;
        }

        bool updated = SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        if (!updated)
        {
            Log.Warning(
                "Windows Mica could not refresh the native window styles. SetWindowPos failed with Win32 error {Error}.",
                Marshal.GetLastWin32Error());
        }

        return updated;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void SetLastError(uint errorCode);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
