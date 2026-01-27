using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Services;
using Tagium.Core.Services.DataExport;
using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryItengrity;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Shared;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

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

        // Core - Undo
        _ = services.AddSingleton<UndoService>();

        // Core - Services
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<LibraryService>();
        _ = services.AddSingleton<LibraryIntegrityService>();
        _ = services.AddSingleton<DataExportService>();
        _ = services.AddSingleton<DatabaseMergeService>();

        // UI - Components
        _ = services.AddTransient<SearchInput>();

        // UI - Services
        _ = services.AddSingleton<AppState>();
        _ = services.AddSingleton<ModalService>();
        _ = services.AddSingleton<FileDialogService>();
        _ = services.AddSingleton<SystemLauncherService>();
        _ = services.AddSingleton<ToastService>();
        _ = services.AddSingleton<FileImportService>();
        _ = services.AddSingleton<KeyboardShortcutService>();
    }
}