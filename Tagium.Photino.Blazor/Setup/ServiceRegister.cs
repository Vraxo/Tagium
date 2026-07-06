using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Services;
using Tagium.Core.Services.DataExport;
using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryIntegrity;
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
        // Logging
        _ = services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

        // Core - Settings
        _ = services.AddSingleton<SettingsManager>();

        // Core - Data
        _ = services.AddSingleton<DatabaseManager>();
        _ = services.AddSingleton<TagRepository>();
        _ = services.AddSingleton<FileRepository>();
        _ = services.AddSingleton<FileTagRepository>();
        _ = services.AddSingleton<SearchQueryCompiler>();
        _ = services.AddSingleton<FileSearchRepository>();
        _ = services.AddSingleton<LibraryRootRepository>();

        // Core - Undo
        _ = services.AddSingleton<UndoService>();

        // Core - Helpers
        _ = services.AddSingleton<FileHasher>();

        // Core - Services
        _ = services.AddSingleton<ExifToolService>();
        _ = services.AddSingleton<ThumbnailCache>();
        _ = services.AddSingleton<ThumbnailGenerator>();
        _ = services.AddSingleton<VideoFrameExtractor>();
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<LibraryScanService>();
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
        _ = services.AddSingleton<KeyboardShortcutService>();
    }
}