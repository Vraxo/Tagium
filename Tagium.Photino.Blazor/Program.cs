using Photino.Blazor;
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

        _ = app.MainWindow
            .SetTitle("Tagium")
            .SetUseOsDefaultSize(false)
            .SetSize(1200, 800)
            .Center();

        app.Run();
    }
}