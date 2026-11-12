using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.NET;
using Serilog;
using Serilog.Events;
using Tagium.Core;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.AppShell;

public class Program
{
    public static PhotinoWindow? MainWindow { get; set; }

    [STAThread]
    [Obsolete]
    private static void Main(string[] args)
    {
        InitializeLogger();

        try
        {
            Log.Information("Starting Tagium Application...");

            PhotinoBlazorAppBuilder appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

            // Register Services
            ServiceRegistrar.ConfigureServices(appBuilder.Services);

            // Register Root Component
            appBuilder.RootComponents.Add<App>("#app");

            PhotinoBlazorApp app = appBuilder.Build();

            // Serve video previews over loopback HTTP so WebView can seek with byte ranges.
            using LocalVideoServer videoServer = app.Services.GetRequiredService<LocalVideoServer>();
            videoServer.Start();

            // Pass ServiceProvider to static SchemeHandler
            SchemeHandlers.ServiceProvider = app.Services;

            // Pass MainWindow to static property for runtime accessibility
            MainWindow = app.MainWindow;

            // Custom Scheme for Thumbnails and Previews
            _ = app.MainWindow.RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);

            // Load saved zoom factor on startup
            SettingsManager settings = app.Services.GetRequiredService<SettingsManager>();
            int savedZoom = settings.Current.Zoom;
            bool useNativeMica = PhotinoWindow.IsWindowsPlatform
                && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                && settings.Current.InterfaceStyle == InterfaceStyle.Windows11;

            if (useNativeMica)
            {
                // Keep Photino's native title bar and window behavior. DWM supplies
                // the genuine Mica system backdrop on supported Windows 11 builds.
                app.MainWindow.WindowCreatedHandler += (_, _) => WindowsMicaWindow.Apply(app.MainWindow);
            }

            _ = app.MainWindow
                .SetTitle("Tagium")
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .Center()
                .SetZoom(savedZoom)
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