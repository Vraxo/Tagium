using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.NET;
using Serilog;
using Serilog.Events;
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

    public static InterfaceStyle StartupInterfaceStyle { get; private set; }

    public static bool SupportsNativeMica { get; private set; }

    public static bool UsesNativeMica { get; private set; }

    private static bool _settingsWindowOpen;

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
            StartupInterfaceStyle = settings.Current.InterfaceStyle;
            SupportsNativeMica = PhotinoWindow.IsWindowsPlatform
                && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
            UsesNativeMica = SupportsNativeMica && StartupInterfaceStyle == InterfaceStyle.Windows11;

            if (UsesNativeMica)
            {
                app.Services.GetRequiredService<WindowsAccentColorService>().Initialize();
                _ = mainWindow.RegisterWindowCreatedHandler((_, _) => WindowsMicaWindow.Initialize(mainWindow));
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
                        .SetLogVerbosity(0);
                });

            if (UsesNativeMica)
            {
                _ = settingsWindow.Window.RegisterWindowCreatedHandler((_, _) =>
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
