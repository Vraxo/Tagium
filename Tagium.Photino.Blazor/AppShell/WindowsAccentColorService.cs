using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using Serilog;

namespace Tagium.Photino.Blazor.AppShell;

/// <summary>
/// Tracks the Windows system accent color for the Windows 11 interface.
/// </summary>
internal sealed class WindowsAccentColorService : IDisposable
{
    private const string AccentRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const uint RegNotifyChangeLastSet = 0x00000004;

    private RegistryKey? _accentKey;
    private EventWaitHandle? _changeEvent;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private bool _initialized;

    public string CurrentColor { get; private set; } = "#42a5f5";

    public event Action<string>? ColorChanged;

    public void Initialize()
    {
        if (_initialized || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            return;
        }

        try
        {
            _accentKey = Registry.CurrentUser.OpenSubKey(AccentRegistryPath, writable: false);
            if (_accentKey is null)
            {
                Log.Warning("Windows accent registry key is unavailable. Using the default accent.");
                return;
            }

            _changeEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            _watchCancellation = new CancellationTokenSource();
            _initialized = true;
            RefreshAccentColor();
            _watchTask = Task.Run(WatchAccentChanges);

            Log.Information("Windows system accent color tracking initialized with {AccentColor}.", CurrentColor);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not initialize Windows system accent color tracking. Using the default accent.");
            Dispose();
        }
    }

    private void WatchAccentChanges()
    {
        if (!OperatingSystem.IsWindows()
            || _accentKey is null
            || _changeEvent is null
            || _watchCancellation is null)
        {
            return;
        }

        WaitHandle[] waitHandles = [_changeEvent, _watchCancellation.Token.WaitHandle];

        while (!_watchCancellation.IsCancellationRequested)
        {
            int result = RegNotifyChangeKeyValue(
                _accentKey.Handle,
                watchSubtree: false,
                RegNotifyChangeLastSet,
                _changeEvent.SafeWaitHandle.DangerousGetHandle(),
                asynchronous: true);

            if (result != 0)
            {
                Log.Warning("Windows accent color monitoring stopped. Registry notification returned {ErrorCode}.", result);
                return;
            }

            if (WaitHandle.WaitAny(waitHandles) == 1)
            {
                return;
            }

            RefreshAccentColor();
        }
    }

    private void RefreshAccentColor()
    {
        if (_accentKey is null)
        {
            return;
        }

        try
        {
            string? hex = ReadAccentColor(_accentKey);
            if (hex is null || string.Equals(CurrentColor, hex, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentColor = hex;
            Log.Information("Windows system accent color changed to {AccentColor}.", hex);
            ColorChanged?.Invoke(hex);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the current Windows system accent color.");
        }
    }

    private static string? ReadAccentColor(RegistryKey key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (key.GetValue("AccentColorMenu") is int accentColor)
        {
            byte red = (byte)accentColor;
            byte green = (byte)(accentColor >> 8);
            byte blue = (byte)(accentColor >> 16);
            return $"#{red:X2}{green:X2}{blue:X2}";
        }

        if (key.GetValue("AccentPalette") is byte[] palette && palette.Length >= 15)
        {
            return $"#{palette[12]:X2}{palette[13]:X2}{palette[14]:X2}";
        }

        return null;
    }

    public void Dispose()
    {
        _watchCancellation?.Cancel();
        _changeEvent?.Set();

        try
        {
            _watchTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Windows accent color watcher stopped during disposal.");
        }

        if (OperatingSystem.IsWindows())
        {
            _accentKey?.Dispose();
        }

        _changeEvent?.Dispose();
        _watchCancellation?.Dispose();

        _accentKey = null;
        _changeEvent = null;
        _watchCancellation = null;
        _watchTask = null;
        _initialized = false;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        IntPtr eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
