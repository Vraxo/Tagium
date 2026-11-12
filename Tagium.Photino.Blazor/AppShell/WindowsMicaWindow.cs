using System.Runtime.InteropServices;
using Photino.NET;
using Serilog;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// Requests the native Windows 11 Mica system backdrop for Tagium's main window.
/// The existing Photino title bar, frame, resizing, and caption controls remain native.
/// </summary>
internal static class WindowsMicaWindow
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    public static bool IsActive { get; private set; }

    public static void Apply(PhotinoWindow window)
    {
        if (!PhotinoWindow.IsWindowsPlatform
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return;
        }

        int backdropType = DwmsbtMainWindow;
        int result = DwmSetWindowAttribute(
            window.WindowHandle,
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
        else
        {
            Log.Information("Enabled the native Windows 11 Mica system backdrop.");
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
