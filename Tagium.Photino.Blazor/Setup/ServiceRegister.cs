using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Services;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Setup;

public static class ServiceRegistrar
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Core - Settings
        _ = services.AddSingleton<SettingsManager>();

        // Core - Data
        _ = services.AddSingleton<DatabaseManager>();
        _ = services.AddSingleton<TagRepository>();
        _ = services.AddSingleton<FileRepository>();
        _ = services.AddSingleton<FileTagRepository>();
        _ = services.AddSingleton<FileSearchRepository>();

        // Core - Services
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<LibraryService>();
        _ = services.AddSingleton<LibraryIntegrityService>();
        _ = services.AddSingleton<DataExportService>();
        _ = services.AddSingleton<DatabaseMergeService>();

        // UI Services
        _ = services.AddSingleton<AppState>();
        _ = services.AddSingleton<ModalService>();
        _ = services.AddSingleton<FileDialogService>();
        _ = services.AddSingleton<SystemLauncherService>();
        _ = services.AddSingleton<ToastService>();
    }
}