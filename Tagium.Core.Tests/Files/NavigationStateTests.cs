using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class NavigationStateTests
{
    [Fact]
    public void StartsOnTheFilesViewWithNoFilter()
    {
        NavigationState nav = new();

        nav.CurrentView.Should().Be(AppView.Files);
        nav.SearchText.Should().BeEmpty();
        nav.IsMissingOnly.Should().BeFalse();
        nav.CurrentFolderPath.Should().BeNull();
        nav.IsFiltered.Should().BeFalse();
    }

    [Fact]
    public void SwitchView_ToTheSameView_DoesNotNotify()
    {
        NavigationState nav = new();
        int changes = 0;
        nav.OnChange += () => changes++;

        nav.SwitchView(AppView.Files);

        changes.Should().Be(0);
    }

    [Fact]
    public void SwitchView_ToADifferentView_Notifies()
    {
        NavigationState nav = new();
        int changes = 0;
        nav.OnChange += () => changes++;

        nav.SwitchView(AppView.Tags);

        nav.CurrentView.Should().Be(AppView.Tags);
        changes.Should().Be(1);
    }

    [Fact]
    public void SetSearch_ClearsTheOtherFiltersAndSwitchesToFiles()
    {
        NavigationState nav = new();
        nav.SetFolder("/lib/photos");
        nav.SetMissingOnly(true);

        nav.SetSearch("cat");

        nav.SearchText.Should().Be("cat");
        nav.IsMissingOnly.Should().BeFalse();
        nav.CurrentFolderPath.Should().BeNull();
        nav.CurrentView.Should().Be(AppView.Files);
        nav.IsFiltered.Should().BeTrue();
    }

    [Fact]
    public void SetMissingOnly_ClearsTheOtherFiltersAndSwitchesToFiles()
    {
        NavigationState nav = new();
        nav.SetSearch("cat");

        nav.SetMissingOnly(true);

        nav.IsMissingOnly.Should().BeTrue();
        nav.SearchText.Should().BeEmpty();
        nav.CurrentFolderPath.Should().BeNull();
        nav.CurrentView.Should().Be(AppView.Files);
    }

    [Fact]
    public void SetFolder_ClearsTheOtherFiltersAndSwitchesToFolders()
    {
        NavigationState nav = new();
        nav.SetSearch("cat");

        nav.SetFolder("/lib/photos");

        nav.CurrentFolderPath.Should().Be("/lib/photos");
        nav.SearchText.Should().BeEmpty();
        nav.IsMissingOnly.Should().BeFalse();
        nav.CurrentView.Should().Be(AppView.Folders);
    }

    [Fact]
    public void SetFolder_WithNull_ClearsTheFolderFilter()
    {
        NavigationState nav = new();
        nav.SetFolder("/lib/photos");

        nav.SetFolder(null);

        nav.CurrentFolderPath.Should().BeNull();
        nav.IsFiltered.Should().BeFalse();
    }

    [Fact]
    public void FilterSetters_RaiseTheirSpecificEvent_SoTheFileListCanReload()
    {
        NavigationState nav = new();
        int filter = 0, folder = 0;
        nav.OnFilterChanged += () => filter++;
        nav.OnFolderChanged += () => folder++;

        nav.SetSearch("cat");
        nav.SetMissingOnly(true);
        filter.Should().Be(2);
        folder.Should().Be(0);

        nav.SetFolder("/lib/photos");
        folder.Should().Be(1);
        filter.Should().Be(2);
    }

    [Fact]
    public void SetSearch_ToAnEmptyString_StillReloads()
    {
        // Clearing the search box must restore the unfiltered list, so this cannot be skipped
        // as a no-op when the text is already empty.
        NavigationState nav = new();
        int filter = 0;
        nav.OnFilterChanged += () => filter++;

        nav.SetSearch("");

        filter.Should().Be(1);
    }

    [Fact]
    public void ReturnToTagGraph_SwitchesToTheTagsGraphView()
    {
        NavigationState nav = new();
        nav.SwitchView(AppView.Files);

        nav.ReturnToTagGraph();

        nav.CurrentView.Should().Be(AppView.Tags);
        nav.IsTagGraphView.Should().BeTrue();
    }

    [Fact]
    public void ReturnToTagGraph_WhenAlreadyThere_DoesNotNotify()
    {
        NavigationState nav = new();
        nav.ReturnToTagGraph();
        int changes = 0;
        nav.OnChange += () => changes++;

        nav.ReturnToTagGraph();

        changes.Should().Be(0);
    }

    [Fact]
    public void SetTagGraphView_OnlyNotifiesOnARealChange()
    {
        NavigationState nav = new();
        int changes = 0;
        nav.OnChange += () => changes++;

        nav.SetTagGraphView(false);
        changes.Should().Be(0);

        nav.SetTagGraphView(true);
        changes.Should().Be(1);
    }

    [Fact]
    public void IsFiltered_IsFalseForAnEmptySearchBlankFolderAndMissingOff()
    {
        NavigationState nav = new();
        nav.SetSearch("");
        nav.IsFiltered.Should().BeFalse();

        nav.SetFolder("");
        nav.IsFiltered.Should().BeFalse();

        nav.SetMissingOnly(true);
        nav.IsFiltered.Should().BeTrue();
    }

    [Fact]
    public void FilterChange_ResetsTheViewportBeforeNotifyingObservers()
    {
        // AppState resets the browser in the filter handler; the ordering matters because the
        // reset must land before the views are told to repaint, or a stale page shows for a frame.
        NavigationState nav = new();
        List<string> order = [];
        nav.OnFilterChanged += () => order.Add("reload");
        nav.OnChange += () => order.Add("notify");

        nav.SetSearch("cat");

        order.Should().Equal("reload", "notify");
    }
}
