using Microsoft.JSInterop;
using System.Text.Json;
using Tagium.Core.Helpers;
using Tagium.Photino.Blazor.Models;

namespace Tagium.Photino.Blazor.Services;

public class ThemeService
{
    public List<Theme> LoadedThemes { get; } = [];

    private readonly string _themesDir;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ThemeService()
    {
        _themesDir = AppDataHelper.GetThemesDirectory();
    }

    public async Task InitializeAsync(IJSRuntime js)
    {
        SyncDefaultThemesFromShippingDir();
        ReloadThemes();
        await Task.CompletedTask;
    }

    public async Task ApplyThemeAsync(IJSRuntime js, string themeName)
    {
        if (string.Equals(themeName, "Default", StringComparison.OrdinalIgnoreCase))
        {
            await js.InvokeVoidAsync("themeManager.applyTheme", (object?)null);
            return;
        }

        Theme? active;
        lock (_lock)
        {
            active = LoadedThemes.FirstOrDefault(t => string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase));
        }

        if (active != null)
        {
            await js.InvokeVoidAsync("themeManager.applyTheme", active.Colors);
        }
    }

    private void ReloadThemes()
    {
        lock (_lock)
        {
            LoadedThemes.Clear();

            if (!Directory.Exists(_themesDir))
            {
                return;
            }

            string[] files = Directory.GetFiles(_themesDir, "*.json");
            foreach (string file in files)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    Theme? theme = JsonSerializer.Deserialize<Theme>(json, _jsonOptions);

                    if (theme != null && !string.IsNullOrWhiteSpace(theme.Name))
                    {
                        // Safely de-duplicate in memory to handle orphaned files on the user's disk
                        bool alreadyLoaded = LoadedThemes.Any(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyLoaded)
                        {
                            LoadedThemes.Add(theme);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ThemeService] Error loading theme file '{file}': {ex.Message}");
                }
            }
        }
    }

    private void SyncDefaultThemesFromShippingDir()
    {
        string shippingThemesDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "themes");
        if (!Directory.Exists(shippingThemesDir) || !Directory.Exists(_themesDir))
        {
            return;
        }

        try
        {
            string[] shippingFiles = Directory.GetFiles(shippingThemesDir, "*.json");
            foreach (string file in shippingFiles)
            {
                string fileName = Path.GetFileName(file);
                string destPath = Path.Combine(_themesDir, fileName);

                // Copy default templates only if missing to prevent overriding user customizations
                if (!File.Exists(destPath))
                {
                    File.Copy(file, destPath, true);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThemeService] Error syncing default themes from shipping folder: {ex.Message}");
        }
    }
}