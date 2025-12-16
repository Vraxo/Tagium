using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Setup;

namespace Tagium.Photino.Blazor;

public class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

        // Register Services
        ServiceRegistrar.ConfigureServices(appBuilder.Services);

        // Register Root Component
        appBuilder.RootComponents.Add<App>("#app");

        var app = appBuilder.Build();

        // Custom Scheme for Thumbnails
        _ = app.MainWindow.RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);

        // Native File Drop Handler
        // Bypasses WebView security to get full file paths
        _ = app.MainWindow.RegisterFileDropHandler((object sender, string[] files) =>
        {
            var libraryService = app.Services.GetService<LibraryService>();
            libraryService?.ImportBatch(files);
        });

        _ = app.MainWindow
            .SetTitle("Tagium")
            .SetUseOsDefaultSize(false)
            .SetSize(1200, 800)
            .Center();

        app.Run();
    }
}