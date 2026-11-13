using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.NET;
using Serilog;
using Serilog.Events;
using System.Drawing;
using Tagium.Core;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor;
using Tagium.Photino.Blazor.Settings;

namespace Tagium.Photino.Blazor.AppShell;

public class Program
{
    public static PhotinoWindow? MainWindow { get; private set; }

    public static PhotinoBlazorApp? Application { get; private set; }

    public static PhotinoWindow? SettingsWindowNative { get; private set; }

    public static InterfaceStyle ActiveInterfaceStyle { get; private set; }

    public static void SetActiveInterfaceStyle(InterfaceStyle style)
    {
        ActiveInterfaceStyle = style;
    }

    public static bool SupportsNativeMica { get; private set; }

    public static bool UsesNativeMica { get; private set; }

    public static bool IsViewOptionsWindowOpen => _viewOptionsWindow is not null;

    public static event Action? ViewOptionsPopupClosed;

    private static bool _settingsWindowOpen;

    private static PhotinoBlazorWindow? _viewOptionsWindow;

    [STAThread]
    [Obsolete]
    private static void Main(string[] args)
    {
        InitializeLogger();

        try
        {
            Log.Information("Starting Tagium Application...");

            if (OperatingSystem.IsWindows()
                && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");
            }

            PhotinoBlazorAppBuilder appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);
            ServiceRegistrar.ConfigureServices(appBuilder.Services);
            appBuilder.RootComponents.Add<App>("#app");

            PhotinoBlazorApp app = appBuilder.Build();
            Application = app;

            using LocalVideoServer videoServer = app.Services.GetRequiredService<LocalVideoServer>();
            videoServer.Start();

            SchemeHandlers.ServiceProvider = app.Services;

            PhotinoBlazorWindow mainBlazorWindow = app.MainBlazorWindow;
            PhotinoWindow mainWindow = mainBlazorWindow.Window;
            MainWindow = mainWindow;
            _ = mainWindow.RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);

            SettingsManager settings = app.Services.GetRequiredService<SettingsManager>();
            ActiveInterfaceStyle = settings.Current.InterfaceStyle;
            SupportsNativeMica = OperatingSystem.IsWindows()
                && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
            UsesNativeMica = SupportsNativeMica && ActiveInterfaceStyle == InterfaceStyle.Windows11;

            if (UsesNativeMica)
            {
                app.Services.GetRequiredService<WindowsAccentColorService>().Initialize();
                _ = mainWindow.RegisterCreatedHandler((_, _) => WindowsMicaWindow.Initialize(mainWindow));
            }

            _ = mainWindow
                .SetTitle("Tagium")
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .Center()
                .SetZoom(settings.Current.Zoom)
                .SetLogVerbosity(0);

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Tagium application terminated unexpectedly.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static void OpenSettingsWindow()
    {
        if (_settingsWindowOpen || Application is null)
        {
            return;
        }

        _settingsWindowOpen = true;

        try
        {
            SettingsManager settings = Application.Services.GetRequiredService<SettingsManager>();
            PhotinoBlazorWindow settingsWindow = Application.CreateWindow<SettingsWindow>(
                "#app",
                configure: window =>
                {
                    window
                        .SetTitle("Tagium Settings")
                        .SetUseOsDefaultSize(false)
                        .SetSize(820, 720)
                        .Center()
                        .SetZoom(settings.Current.Zoom)
                        .SetLogVerbosity(0)
                        .RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);
                });

            if (UsesNativeMica)
            {
                _ = settingsWindow.Window.RegisterCreatedHandler((_, _) =>
                    WindowsMicaWindow.Initialize(settingsWindow.Window));
            }

            SettingsWindowNative = settingsWindow.Window;
            settingsWindow.Show();
        }
        catch
        {
            _settingsWindowOpen = false;
            throw;
        }
    }

    public static bool OpenViewOptionsWindow(ViewOptionsAnchor anchor)
    {
        if (!UsesNativeMica
            || Application is null
            || MainWindow is null
            || _viewOptionsWindow is not null)
        {
            return false;
        }

        try
        {
            SettingsManager settings = Application.Services.GetRequiredService<SettingsManager>();
            int zoom = settings.Current.Zoom;
            int width = (int)Math.Round(296d * zoom / 100d);
            int height = (int)Math.Round(252d * zoom / 100d);
            Point location = WindowsPopupPosition.GetViewOptionsLocation(MainWindow, anchor, width, height);

            PhotinoBlazorWindow popup = Application.CreateWindow<ViewOptionsPopup>(
                "#app",
                configure: window =>
                {
                    window
                        .SetTitle("Tagium View Options")
                        .SetUseOsDefaultSize(false)
                        .SetUseOsDefaultLocation(false)
                        .SetSize(width, height)
                        .SetLocation(location)
                        .SetResizable(false)
                        .SetChromeless(true)
                        .SetUseNativeWindowOwner(true)
                        .SetZoom(zoom)
                        .SetLogVerbosity(0)
                        .RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);
                });

            _viewOptionsWindow = popup;

            _ = popup.Window.RegisterCreatedHandler((_, _) =>
                WindowsAcrylicWindow.Initialize(popup.Window));
            _ = popup.Window.RegisterDeactivatedHandler((_, _) => CloseViewOptionsWindow());
            _ = popup.Window.RegisterClosedHandler((_, _) =>
                OnViewOptionsWindowClosed(popup.Window));

            popup.Show();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open the native View options popup.");
            PhotinoBlazorWindow? popup = _viewOptionsWindow;
            _viewOptionsWindow = null;

            if (popup?.Window is { IsInitialized: true, IsClosed: false })
            {
                try
                {
                    popup.Window.Close();
                }
                catch (Exception closeException)
                {
                    Log.Warning(closeException, "Could not close the failed View options popup.");
                }
            }

            ViewOptionsPopupClosed?.Invoke();
            return false;
        }
    }

    public static void CloseViewOptionsWindow()
    {
        PhotinoBlazorWindow? popup = _viewOptionsWindow;
        if (popup is null)
        {
            return;
        }

        if (popup.Window.IsInitialized && !popup.Window.IsClosed)
        {
            popup.Window.Close();
            return;
        }

        OnViewOptionsWindowClosed(popup.Window);
    }

    private static void OnViewOptionsWindowClosed(PhotinoWindow window)
    {
        if (_viewOptionsWindow?.Window != window)
        {
            return;
        }

        _viewOptionsWindow = null;
        ViewOptionsPopupClosed?.Invoke();
    }

    public static void SettingsWindowClosed()
    {
        SettingsWindowNative = null;
        _settingsWindowOpen = false;
    }

    private static void InitializeLogger()
    {
        string baseDir = AppDataHelper.GetBaseDirectory();
        string logPath = Path.Combine(baseDir, "app.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
    }
}
