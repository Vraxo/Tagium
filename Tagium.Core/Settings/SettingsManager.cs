using System.Text.Json;
using System.Text.Json.Serialization;
using Tagium.Core;
using Tagium.Core.Search;

namespace Tagium.Core.Settings;

public class SettingsManager
{
    public const int MaxRecentTags = 5;
    public const int MaxRecentSearches = SearchHistory.DefaultLimit;

    public Settings Current { get; }

    private readonly string _settingsPath;

    public event Action? SettingsChanged;

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public SettingsManager()
        : this(Path.Combine(AppDataHelper.GetBaseDirectory(), "settings.json"))
    {
    }

    /// <summary>Uses an explicit settings file, allowing isolated storage when needed.</summary>
    public SettingsManager(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
        Current = Load();
    }


    public void Update(Action<Settings> updateAction, bool save = true)
    {
        updateAction(Current);

        if (save)
        {
            Save();
        }

        SettingsChanged?.Invoke();
    }

    public void RecordRecentTag(Guid tagId)
    {
        Update(settings =>
        {
            settings.RecentTagIds ??= [];
            settings.RecentTagIds.RemoveAll(id => id == tagId);
            settings.RecentTagIds.Insert(0, tagId);

            if (settings.RecentTagIds.Count > MaxRecentTags)
            {
                settings.RecentTagIds.RemoveRange(
                    MaxRecentTags,
                    settings.RecentTagIds.Count - MaxRecentTags);
            }
        });
    }

    /// <summary>
    /// Remembers a committed search. The ordering, dedupe and cap rules live in
    /// <see cref="SearchHistory"/>; this only decides when the file is worth rewriting.
    /// </summary>
    public void RecordRecentSearch(string? query)
    {
        List<string> current = Current.RecentSearches ?? [];
        IReadOnlyList<string> updated = SearchHistory.Record(current, query, MaxRecentSearches);

        if (updated.SequenceEqual(current, StringComparer.Ordinal))
        {
            return;
        }

        Update(settings => settings.RecentSearches = [.. updated]);
    }

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

    private Settings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new Settings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<Settings>(json, _serializerOptions)
                ?? new Settings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error loading: {ex.Message}. Using defaults.");
            return new Settings();
        }
    }
}