using System.Text.Json;
using System.Text.Json.Serialization;
using Tagium.Core.Helpers;

namespace Tagium.Core.Settings;

public class SettingsManager
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsManager()
    {
        string baseDir = AppDataHelper.GetBaseDirectory();
        _settingsPath = Path.Combine(baseDir, "settings.json");
        Current = Load();
    }

    public TagiumSettings Current { get; }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, _serializerOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error saving: {ex.Message}");
        }
    }

    private TagiumSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new TagiumSettings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<TagiumSettings>(json, _serializerOptions) ?? new TagiumSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error loading: {ex.Message}. Using defaults.");
            return new TagiumSettings();
        }
    }
}