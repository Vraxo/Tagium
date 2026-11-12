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
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;

    public static bool IsActive { get; private set; }

    public static void Apply(PhotinoWindow window)
    {
        SetEnabled(window, true);
    }

    public static void SetEnabled(PhotinoWindow window, bool enabled)
    {
        if (!PhotinoWindow.IsWindowsPlatform
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            IsActive = false;
            return;
        }

        int backdropType = enabled ? DwmsbtMainWindow : DwmsbtNone;
        int result = DwmSetWindowAttribute(
            window.WindowHandle,
            DwmwaSystemBackdropType,
            ref backdropType,
            sizeof(int));

        IsActive = enabled && result >= 0;
        if (result < 0)
        {
            Log.Warning(
                "Could not {Action} the Windows 11 Mica backdrop. DwmSetWindowAttribute returned HRESULT 0x{HResult:X8}.",
                enabled ? "enable" : "disable",
                result);
        }
        else
        {
            Log.Information(
                "{Action} the native Windows 11 Mica system backdrop.",
                enabled ? "Enabled" : "Disabled");
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
