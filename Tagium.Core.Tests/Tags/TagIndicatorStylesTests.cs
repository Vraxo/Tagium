using System.Text.RegularExpressions;

namespace Tagium.Core.Tests.Tags;

/// <summary>
/// Repository-level checks for the tag indicators shared by the sidebar, selector and manager.
/// These guard against restoring a conspicuous neutral ring or losing the color button's focus cue.
/// </summary>
public class TagIndicatorStylesTests
{
    [Theory]
    [InlineData("TagIcon.razor.css", ".tag-color", "8px")]
    [InlineData("TagManagerView.razor.css", ".color-circle", "20px")]
    public void Indicator_UsesThinTagTintedEdge(string fileName, string selector, string size)
    {
        string css = ReadCss(fileName);
        Match rule = Regex.Match(css, @"(?m)^\s*" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(rule.Success, $"Could not find {selector} in {fileName}");

        string body = rule.Groups["body"].Value;
        Assert.Matches(@"background-color:\s*var\(--tag-color\)\s*;", body);
        Assert.Matches(@"width:\s*" + size + @"\s*;", body);
        Assert.Matches(@"height:\s*" + size + @"\s*;", body);
        Assert.Matches(@"box-sizing:\s*border-box\s*;", body);
        Assert.Matches(@"border-radius:\s*50%\s*;", body);
        // The first declaration is also the fallback for WebViews without color-mix support.
        Assert.Matches(@"border:\s*1px\s+solid\s+var\(--tag-color\)\s*;", body);
        Assert.Matches(
            @"border-color:\s*color-mix\(in\s+srgb,\s*var\(--tag-color\)\s+50%,\s*var\(--text-muted\)\)\s*;",
            body);
        Assert.True(
            body.IndexOf("border:", StringComparison.Ordinal) < body.IndexOf("border-color:", StringComparison.Ordinal),
            "The tag-colour fallback must precede the blended border.");
        Assert.DoesNotContain("box-shadow:", body);
        Assert.DoesNotContain("outline:", body);
    }

    [Fact]
    public void ColorButton_KeepsVisibleKeyboardFocus()
    {
        string css = ReadCss("TagManagerView.razor.css");
        Match? focusRule = Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")
            .FirstOrDefault(rule => rule.Groups["selector"].Value.Contains(".color-circle:focus-visible", StringComparison.Ordinal));

        Assert.NotNull(focusRule);
        Assert.Matches(@"outline:\s*2px\s+solid\s+var\(--primary-color\)\s*;", focusRule.Groups["body"].Value);
        Assert.Matches(@"outline-offset:\s*2px\s*;", focusRule.Groups["body"].Value);
    }

    private static string ReadCss(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tagium.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string path = Path.Combine(directory.FullName, "Tagium.Photino.Blazor", "Tags", fileName);
        return Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }
}
