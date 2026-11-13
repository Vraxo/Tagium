using System.Runtime.InteropServices;
using Photino.NET;
using Serilog;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// Applies the Windows 11 system backdrop while preserving the native decorated window frame.
/// </summary>
internal static class WindowsMicaWindow
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    public static bool IsActive { get; private set; }

    public static void Initialize(PhotinoWindow window)
    {
        if (!OperatingSystem.IsWindows()
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return;
        }

        IntPtr windowHandle = window.HWND;
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
                "Windows Mica could not extend the DWM frame into the client area. HRESULT 0x{HResult:X8}.",
                frameResult);
        }

        int backdropType = DwmsbtMainWindow;
        int backdropResult = DwmSetWindowAttribute(
            windowHandle,
            DwmwaSystemBackdropType,
            ref backdropType,
            sizeof(int));

        if (backdropResult < 0)
        {
            Log.Warning(
                "Windows Mica could not be enabled. DwmSetWindowAttribute returned HRESULT 0x{HResult:X8}.",
                backdropResult);
        }

        IsActive = frameResult >= 0 && backdropResult >= 0;
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
