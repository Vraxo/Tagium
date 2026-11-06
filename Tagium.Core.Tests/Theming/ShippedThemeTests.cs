using System.Text.Json;
using System.Text.RegularExpressions;
using Tagium.Core.Theming;

namespace Tagium.Core.Tests.Theming;

/// <summary>
/// Repository-level guardrails for the shipped theme files. They read the themes straight from
/// the checkout so a theme edit cannot ship a token the UI styles but no theme defines (the
/// <c>--surface-hover</c> miss), a typo'd token name, or a text pair below WCAG AA.
/// </summary>
public class ShippedThemeTests
{
    /// <summary>
    /// The colour contract every shipped theme has to satisfy. Mirrors the colour table in
    /// Tagium.Photino.Blazor/design.md; adding a token means adding it here too.
    /// </summary>
    private static readonly string[] RequiredTokens =
    [
        "--bg-color",
        "--panel-bg",
        "--surface-bg",
        "--surface-hover",
        "--border-color",
        "--text-color",
        "--text-muted",
        "--primary-color",
        "--primary-hover",
        "--primary-tint",
        "--text-on-primary",
        "--danger-color",
        "--danger-hover",
        "--text-on-danger",
        "--success-color",
        "--folder-color",
        "--warning-color",
        "--appearance-color",
    ];

    /// <summary>
    /// Foreground/background pairs that must stay readable in every theme. Hover pairs sit on a
    /// slightly lower floor: they are transient states and some palettes lighten rather than darken.
    /// </summary>
    private static readonly (string Foreground, string Background, double Minimum)[] TextPairs =
    [
        ("--text-color", "--bg-color", 4.5),
        ("--text-color", "--panel-bg", 4.5),
        ("--text-color", "--surface-bg", 4.5),
        ("--text-muted", "--bg-color", 4.5),
        ("--text-muted", "--panel-bg", 4.5),
        ("--text-muted", "--surface-bg", 4.5),
        ("--text-on-primary", "--primary-color", 4.5),
        ("--text-on-primary", "--primary-hover", 4.0),
        ("--text-on-danger", "--danger-color", 4.5),
        ("--text-on-danger", "--danger-hover", 4.0),
    ];

    [Fact]
    public void RootPalette_DefinesEveryRequiredToken()
    {
        Dictionary<string, string> root = LoadRootPalette();

        string[] missing = RequiredTokens.Where(token => !root.ContainsKey(token)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"app.css :root is missing required token(s): {string.Join(", ", missing)}");
    }

    [Fact]
    public void ShippedThemes_DefineExactlyTheRequiredTokens()
    {
        List<string> problems = [];
        HashSet<string> required = new(RequiredTokens, StringComparer.Ordinal);

        foreach (ThemeFile theme in LoadShippedThemes())
        {
            string[] missing = required.Except(theme.Colors.Keys).OrderBy(token => token).ToArray();
            string[] unknown = theme.Colors.Keys.Except(required, StringComparer.Ordinal).OrderBy(token => token).ToArray();

            if (missing.Length > 0)
            {
                problems.Add($"{theme.FileName}: missing {string.Join(", ", missing)}");
            }

            if (unknown.Length > 0)
            {
                problems.Add($"{theme.FileName}: unknown token(s) {string.Join(", ", unknown)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ShippedThemes_MeetWcagAaForTextPairs()
    {
        List<string> problems = [];

        foreach (ThemeFile theme in LoadShippedThemes())
        {
            problems.AddRange(FindContrastProblems(theme.Name, theme.Colors));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void RootPalette_MeetsWcagAaForTextPairs()
    {
        List<string> problems = FindContrastProblems("Default (app.css :root)", LoadRootPalette()).ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static IEnumerable<string> FindContrastProblems(
        string label,
        IReadOnlyDictionary<string, string> colors)
    {
        foreach ((string foreground, string background, double minimum) in TextPairs)
        {
            // Missing tokens are reported by ShippedThemes_DefineExactlyTheRequiredTokens.
            if (!colors.TryGetValue(foreground, out string? foregroundValue) ||
                !colors.TryGetValue(background, out string? backgroundValue))
            {
                continue;
            }

            double ratio = ColorContrast.GetContrastRatio(foregroundValue, backgroundValue);

            if (ratio < minimum)
            {
                yield return $"{label}: {foreground} on {background} = {ratio:F2}:1 (needs {minimum:F1}:1)";
            }
        }
    }

    private static Dictionary<string, string> LoadRootPalette()
    {
        string cssPath = Path.Combine(GetRepoRoot(), "Tagium.Photino.Blazor", "wwwroot", "css", "app.css");
        // Comments are stripped first so prose inside :root can never look like a declaration.
        string css = Regex.Replace(File.ReadAllText(cssPath), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        Match rootBlock = Regex.Match(css, @":root\s*\{([^}]*)\}");
        Assert.True(rootBlock.Success, $"Could not find the :root block in {cssPath}");

        return Regex.Matches(rootBlock.Groups[1].Value, @"(--[a-z0-9-]+)\s*:\s*([^;]+);")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim());
    }

    private static List<ThemeFile> LoadShippedThemes()
    {
        string themesDirectory = Path.Combine(GetRepoRoot(), "Tagium.Photino.Blazor", "wwwroot", "themes");
        Assert.True(Directory.Exists(themesDirectory), $"Theme directory not found: {themesDirectory}");

        List<ThemeFile> themes = [];

        foreach (string file in Directory.GetFiles(themesDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));

            string name = document.RootElement.TryGetProperty("Name", out JsonElement nameElement)
                ? nameElement.GetString() ?? Path.GetFileName(file)
                : Path.GetFileName(file);

            Dictionary<string, string> colors = document.RootElement.GetProperty("Colors")
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);

            themes.Add(new ThemeFile(Path.GetFileName(file), name, colors));
        }

        Assert.True(themes.Count > 0, $"No theme files found in {themesDirectory}");
        return themes;
    }

    /// <summary>
    /// Tests run from the build output directory, so walk up until the solution file is found
    /// instead of relying on the working directory.
    /// </summary>
    private static string GetRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tagium.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate Tagium.slnx above {AppContext.BaseDirectory}");
    }

    private sealed record ThemeFile(string FileName, string Name, Dictionary<string, string> Colors);
}
