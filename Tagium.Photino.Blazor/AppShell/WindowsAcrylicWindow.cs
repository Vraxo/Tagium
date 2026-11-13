using System.Runtime.InteropServices;
using Photino.NET;
using Serilog;

namespace Tagium.Photino.Blazor.AppShell;

internal static class WindowsAcrylicWindow
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static bool Initialize(PhotinoWindow window)
    {
        if (!OperatingSystem.IsWindows()
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return false;
        }

        IntPtr windowHandle = window.WindowHandle;
        Margins margins = new()
        {
            Left = -1,
            Right = -1,
            Top = -1,
            Bottom = -1
        };

        int frameResult = DwmExtendFrameIntoClientArea(windowHandle, ref margins);
        if (frameResult < 0)
        {
            Log.Warning(
                "Windows Acrylic could not extend the DWM frame into the client area. HRESULT 0x{HResult:X8}.",
                frameResult);
        }

        int backdropType = DwmsbtTransientWindow;
        int backdropResult = DwmSetWindowAttribute(
            windowHandle,
            DwmwaSystemBackdropType,
            ref backdropType,
            sizeof(int));

        if (backdropResult < 0)
        {
            Log.Warning(
                "Windows Acrylic could not be enabled. DwmSetWindowAttribute returned HRESULT 0x{HResult:X8}.",
                backdropResult);
        }

        int cornerPreference = DwmwcpRound;
        int cornerResult = DwmSetWindowAttribute(
            windowHandle,
            DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        if (cornerResult < 0)
        {
            Log.Debug(
                "Windows Acrylic popup corner preference was unavailable. HRESULT 0x{HResult:X8}.",
                cornerResult);
        }

        return frameResult >= 0 && backdropResult >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(
        IntPtr windowHandle,
        ref Margins margins);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
