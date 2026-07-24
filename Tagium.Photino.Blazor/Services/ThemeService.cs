using Microsoft.JSInterop;
using System.Text.Json;
using Tagium.Core.Helpers;
using Tagium.Photino.Blazor.Models;

namespace Tagium.Photino.Blazor.Services;

public class ThemeService
{
    public List<Theme> LoadedThemes { get; } = [];

    private readonly string _themesDir;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ThemeService()
    {
        _themesDir = AppDataHelper.GetThemesDirectory();
    }

    public async Task InitializeAsync(IJSRuntime js)
    {
        SeedDefaultThemesIfEmpty();
        await ReloadThemesAsync();
    }

    public async Task ApplyThemeAsync(IJSRuntime js, string themeName)
    {
        if (string.Equals(themeName, "Default", StringComparison.OrdinalIgnoreCase))
        {
            await js.InvokeVoidAsync("themeManager.applyTheme", (object?)null);
            return;
        }

        Theme? active = LoadedThemes.FirstOrDefault(t => string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase));
        if (active != null)
        {
            await js.InvokeVoidAsync("themeManager.applyTheme", active.Colors);
        }
    }

    private async Task ReloadThemesAsync()
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
                string json = await File.ReadAllTextAsync(file);
                Theme? theme = JsonSerializer.Deserialize<Theme>(json, _jsonOptions);
                if (theme != null && !string.IsNullOrWhiteSpace(theme.Name))
                {
                    LoadedThemes.Add(theme);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ThemeService] Error loading theme file '{file}': {ex.Message}");
            }
        }
    }

    private void SeedDefaultThemesIfEmpty()
    {
        if (!Directory.Exists(_themesDir))
        {
            return;
        }

        if (Directory.GetFiles(_themesDir, "*.json").Length > 0)
        {
            return;
        }

        // 1. Classic Light Theme
        Theme classicLight = new()
        {
            Name = "Classic Light",
            Colors = new Dictionary<string, string>
            {
                { "--bg-color", "#F5F5F5" },
                { "--panel-bg", "#EAEAEA" },
                { "--surface-bg", "#FFFFFF" },
                { "--border-color", "#DCDCDC" },
                { "--text-color", "#212121" },
                { "--text-muted", "#666666" },
                { "--primary-color", "#1976D2" },
                { "--primary-hover", "#1565C0" },
                { "--danger-color", "#D32F2F" },
                { "--danger-hover", "#C62828" },
                { "--folder-color", "#E65100" },
                { "--warning-color", "#F57C00" },
                { "--appearance-color", "#8E24AA" }
            }
        };

        // 2. Nordic Frost Theme (Teal/Blue Dark theme)
        Theme nordicFrost = new()
        {
            Name = "Nordic Frost",
            Colors = new Dictionary<string, string>
            {
                { "--bg-color", "#0F141C" },
                { "--panel-bg", "#161B25" },
                { "--surface-bg", "#1F2430" },
                { "--border-color", "#2D323F" },
                { "--text-color", "#E0E6F0" },
                { "--text-muted", "#808D9C" },
                { "--primary-color", "#39ADB5" },
                { "--primary-hover", "#2E9AA1" },
                { "--danger-color", "#F07178" },
                { "--danger-hover", "#E55B62" },
                { "--folder-color", "#FFB454" },
                { "--warning-color", "#FF8F40" },
                { "--appearance-color", "#C249C4" }
            }
        };

        try
        {
            File.WriteAllText(Path.Combine(_themesDir, "classic_light.json"), JsonSerializer.Serialize(classicLight, _jsonOptions));
            File.WriteAllText(Path.Combine(_themesDir, "nordic_frost.json"), JsonSerializer.Serialize(nordicFrost, _jsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThemeService] Error seeding default themes: {ex.Message}");
        }
    }
}