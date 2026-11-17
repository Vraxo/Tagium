using AwesomeAssertions;
using Tagium.Core.Files;
using Tagium.Core.Settings;

namespace Tagium.Core.Tests.Files;

public class FileBrowserTests
{
    private static List<FileEntry> MakeFiles(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new FileEntry { Id = Guid.NewGuid(), Path = $"/lib/file{i}.jpg", Name = $"file{i}.jpg" })];

    private static FileBrowser Create(int pageSize = 50, FileScrollMode mode = FileScrollMode.Paginated) =>
        new(new Settings { FilesPerPage = pageSize, FileScrollMode = mode });

    private static FileBrowser Loaded(int total, int pageSize = 50, FileScrollMode mode = FileScrollMode.Paginated)
    {
        FileBrowser browser = Create(pageSize, mode);
        browser.ApplyLoadedFiles(MakeFiles(total));
        return browser;
    }

    // --- Result set and clamping ----------------------------------------------------------

    [Fact]
    public void ApplyLoadedFiles_EmptySet_LeavesPageAtOne()
    {
        FileBrowser browser = Loaded(0);

        browser.TotalFilesCount.Should().Be(0);
        browser.CurrentPage.Should().Be(1);
        browser.GetVisibleFiles().Should().BeEmpty();
    }

    [Fact]
    public void ApplyLoadedFiles_ShrinkingSet_ClampsThePage()
    {
        FileBrowser browser = Loaded(total: 500, pageSize: 50);
        browser.SetPage(10);

        browser.ApplyLoadedFiles(MakeFiles(60));

        browser.CurrentPage.Should().Be(2); // 60 files / 50 per page
    }

    [Fact]
    public void ApplyLoadedFiles_KeepsLoadedBatches_AcrossRefreshes()
    {
        FileBrowser browser = Loaded(total: 300, pageSize: 100, mode: FileScrollMode.Infinite);
        browser.LoadMoreFiles();
        browser.VisibleFileCount.Should().Be(200);

        // A tag edit or rating reloads the same result set; the user must not lose their place.
        browser.ApplyLoadedFiles(MakeFiles(300));

        browser.VisibleFileCount.Should().Be(200);
    }

    // --- Rendered slice -------------------------------------------------------------------

    [Fact]
    public void GetVisibleFiles_Paginated_ReturnsOnlyTheCurrentPage()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);
        browser.SetPage(2);

        browser.GetVisibleFiles().Should().HaveCount(100);
    }

    [Fact]
    public void GetVisibleFiles_LastPage_IsShort()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);
        browser.SetPage(3);

        browser.GetVisibleFiles().Should().HaveCount(50);
    }

    [Fact]
    public void GetVisibleFiles_Infinite_ReturnsEverythingLoadedFromTheTop()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100, mode: FileScrollMode.Infinite);

        browser.GetVisibleFiles().Should().HaveCount(100);

        browser.LoadMoreFiles();
        browser.GetVisibleFiles().Should().HaveCount(200);

        // Always from the top: infinite scroll keeps earlier batches in the DOM.
        browser.GetVisibleFiles().First().Should().Be(browser.Files[0]);
    }

    // --- Pagination -----------------------------------------------------------------------

    [Fact]
    public void SetPage_OutOfBounds_IsIgnored()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);

        browser.SetPage(0);
        browser.CurrentPage.Should().Be(1);

        browser.SetPage(99);
        browser.CurrentPage.Should().Be(1);

        browser.SetPage(3);
        browser.CurrentPage.Should().Be(3);
    }

    [Fact]
    public void NextPage_And_PrevPage_StayInsideTheBounds()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);

        browser.PrevPage();
        browser.CurrentPage.Should().Be(1);

        browser.NextPage();
        browser.CurrentPage.Should().Be(2);

        browser.NextPage();
        browser.NextPage(); // only 3 pages
        browser.CurrentPage.Should().Be(3);
    }

    [Fact]
    public void TotalPages_RoundsUp()
    {
        Loaded(total: 250, pageSize: 100).TotalPages.Should().Be(3);
        Loaded(total: 200, pageSize: 100).TotalPages.Should().Be(2);
        Loaded(total: 0, pageSize: 100).TotalPages.Should().Be(0);
    }

    // --- Infinite scroll ------------------------------------------------------------------

    [Fact]
    public void LoadMoreFiles_AppendsOneBatch_AndStopsAtTheEnd()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100, mode: FileScrollMode.Infinite);

        browser.HasMoreFiles.Should().BeTrue();
        browser.LoadMoreFiles().Should().BeTrue();
        browser.VisibleFileCount.Should().Be(200);

        browser.LoadMoreFiles().Should().BeTrue();
        browser.VisibleFileCount.Should().Be(250);

        browser.HasMoreFiles.Should().BeFalse();
        browser.LoadMoreFiles().Should().BeFalse();
    }

    [Fact]
    public void HasMoreFiles_IsAlwaysFalseWhenPaginated()
    {
        Loaded(total: 250, pageSize: 100).HasMoreFiles.Should().BeFalse();
    }

    // --- SetPageSize is a request, not an application --------------------------------------

    [Fact]
    public void SetPageSize_RaisesARequest_WithoutApplying()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);
        int? requested = null;
        browser.NeedsPageSizeChange += size => requested = size;

        browser.SetPageSize(250);

        requested.Should().Be(250);
        // The size is a shared setting: it is applied when the settings manager reports back.
        browser.PageSize.Should().Be(100);
    }

    [Fact]
    public void SetPageSize_IgnoresUnsupportedAndUnchangedSizes()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);
        int calls = 0;
        browser.NeedsPageSizeChange += _ => calls++;

        browser.SetPageSize(100);   // unchanged
        browser.SetPageSize(7);     // not a supported size

        calls.Should().Be(0);
    }

    // --- Settings application --------------------------------------------------------------

    [Fact]
    public void ApplySettings_SameModeAndSize_ChangesNothing()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);

        browser.ApplySettings(FileScrollMode.Paginated, 100).Should().BeFalse();
    }

    [Fact]
    public void ApplySettings_ToInfinite_RendersBatchesThroughTheAnchorBatch()
    {
        FileBrowser browser = Loaded(total: 1000, pageSize: 100);
        browser.SetPage(5); // files 400-499

        browser.ApplySettings(FileScrollMode.Infinite, 100).Should().BeTrue();

        browser.IsInfiniteScroll.Should().BeTrue();
        // Whole batches through the anchor's batch, so the page the user came from stays filled.
        browser.VisibleFileCount.Should().Be(500);
    }

    [Fact]
    public void ApplySettings_BackToPaginated_LandsOnThePageHoldingTheAnchor()
    {
        FileBrowser browser = Loaded(total: 1000, pageSize: 100, mode: FileScrollMode.Infinite);
        browser.LoadMoreFiles();
        browser.LoadMoreFiles(); // 300 loaded
        browser.LoadMoreFiles(); // 400 loaded

        browser.ApplySettings(FileScrollMode.Paginated, 100).Should().BeTrue();

        browser.IsInfiniteScroll.Should().BeFalse();
        // The anchor is the top of the rendered window when nothing is selected.
        browser.CurrentPage.Should().Be(1);
    }

    [Fact]
    public void ApplySettings_PageSizeChange_KeepsTheCurrentPageInView()
    {
        FileBrowser browser = Loaded(total: 1000, pageSize: 100);
        browser.SetPage(5); // file 400 is first on the page

        browser.ApplySettings(FileScrollMode.Paginated, 50).Should().BeTrue();

        browser.PageSize.Should().Be(50);
        // File 400 must still be rendered: page 9 of 50-per-page covers 400-449.
        browser.CurrentPage.Should().Be(9);
        browser.GetVisibleFiles().First().Should().Be(browser.Files[400]);
    }

    [Fact]
    public void ApplySettings_ToInfiniteWithNoSelection_SelectsAndFocusesTheAnchor()
    {
        FileBrowser browser = Loaded(total: 1000, pageSize: 100);
        browser.SetPage(3); // files 200-299, nothing selected

        browser.ApplySettings(FileScrollMode.Infinite, 100);

        // The anchor (top of the viewport) becomes the tab stop so arrow keys resume there.
        browser.SelectedFileIds.Should().ContainSingle();
        browser.SelectedFileIds.Should().Contain(browser.Files[200].Id);
        browser.PendingFocusIndex.Should().Be(200);
    }

    [Fact]
    public void ApplySettings_DoesNotDiscardAnExistingSelection()
    {
        FileBrowser browser = Loaded(total: 1000, pageSize: 100);
        browser.SetPage(3);
        Guid chosen = browser.Files[250].Id;
        browser.SelectFile(chosen, multiSelect: false, rangeSelect: false);

        browser.ApplySettings(FileScrollMode.Infinite, 100);

        browser.SelectedFileIds.Should().ContainSingle();
        browser.SelectedFileIds.Should().Contain(chosen);
    }

    // --- Movement and focus ----------------------------------------------------------------

    [Fact]
    public void MovePage_Forward_AtThePageEdge_GoesToTheNextPage()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);
        browser.SelectFile(browser.Files[99].Id, multiSelect: false, rangeSelect: false);

        browser.MovePage(1).Should().BeTrue();

        browser.CurrentPage.Should().Be(2);
        browser.SelectedFileIds.Should().Contain(browser.Files[100].Id);
        browser.PendingFocusIndex.Should().Be(0);
    }

    [Fact]
    public void MovePage_Backward_LandsOnTheLastFileOfThePreviousPage()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);
        browser.SetPage(2);
        browser.SelectFile(browser.Files[100].Id, multiSelect: false, rangeSelect: false);

        browser.MovePage(-1).Should().BeTrue();

        browser.CurrentPage.Should().Be(1);
        browser.SelectedFileIds.Should().Contain(browser.Files[99].Id);
        browser.PendingFocusIndex.Should().Be(99);
    }

    [Fact]
    public void MovePage_AtTheVeryStart_ReportsNoMovement()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);

        browser.MovePage(-1).Should().BeFalse();
    }

    [Fact]
    public void MoveInfinite_LoadsBatchesUntilTheDestinationIsRendered()
    {
        FileBrowser browser = Loaded(total: 1000, pageSize: 100, mode: FileScrollMode.Infinite);
        browser.SelectFile(browser.Files[90].Id, multiSelect: false, rangeSelect: false);

        // One screenful (Moving "batch") forward lands at index 190, which is not loaded yet.
        browser.MoveBatch(1).Should().BeTrue();

        browser.VisibleFileCount.Should().BeGreaterThanOrEqualTo(191);
        browser.SelectedFileIds.Should().Contain(browser.Files[190].Id);
        browser.PendingFocusIndex.Should().Be(190);
    }

    [Fact]
    public void ConsumePendingFocusIndex_ClearsAfterReading()
    {
        FileBrowser browser = Loaded(total: 250, pageSize: 100);
        browser.SelectFile(browser.Files[99].Id, multiSelect: false, rangeSelect: false);
        browser.MovePage(1);

        browser.ConsumePendingFocusIndex().Should().Be(0);
        browser.ConsumePendingFocusIndex().Should().Be(-1);
    }

    [Fact]
    public void ResetScrollPosition_ReturnsToTheTopAndClearsFocus()
    {
        FileBrowser browser = Loaded(total: 1000, pageSize: 100);
        browser.SetPage(5);
        browser.MovePage(1);

        browser.ResetScrollPosition();

        browser.CurrentPage.Should().Be(1);
        browser.VisibleFileCount.Should().Be(100);
        browser.PendingFocusIndex.Should().Be(-1);
    }

    // --- Selection ------------------------------------------------------------------------

    [Fact]
    public void SelectFile_ShiftClick_SpansAcrossPageBoundaries()
    {
        FileBrowser browser = Loaded(total: 500, pageSize: 100);
        browser.SelectFile(browser.Files[95].Id, multiSelect: false, rangeSelect: false);
        browser.SelectFile(browser.Files[105].Id, multiSelect: false, rangeSelect: true);

        // The span is over the whole filtered set, not just the rendered page.
        browser.SelectedFileIds.Should().HaveCount(11);
    }

    [Fact]
    public void ClearSelection_RaisesSelectionChange()
    {
        FileBrowser browser = Loaded(total: 100, pageSize: 50);
        browser.SelectFile(browser.Files[0].Id, multiSelect: false, rangeSelect: false);
        int changes = 0;
        browser.OnSelectionChange += () => changes++;

        browser.ClearSelection();

        changes.Should().Be(1);
        browser.SelectedFileIds.Should().BeEmpty();
    }

    [Fact]
    public void ClearSelectionSilently_DoesNotRaiseSelectionChange()
    {
        FileBrowser browser = Loaded(total: 100, pageSize: 50);
        browser.SelectFile(browser.Files[0].Id, multiSelect: false, rangeSelect: false);
        int changes = 0;
        browser.OnSelectionChange += () => changes++;

        browser.ClearSelectionSilently();

        // A filter change already repaints the list; a second event would repaint every row twice.
        changes.Should().Be(0);
        browser.SelectedFileIds.Should().BeEmpty();
    }

    [Fact]
    public void ClearSelection_WithNothingSelected_DoesNotNotify()
    {
        FileBrowser browser = Loaded(total: 100, pageSize: 50);
        int changes = 0;
        browser.OnSelectionChange += () => changes++;

        browser.ClearSelection();

        changes.Should().Be(0);
    }

    // --- Page size is read from settings at construction -----------------------------------

    [Fact]
    public void Construction_ReadsPageSizeAndModeFromSettings()
    {
        FileBrowser browser = Create(pageSize: 250, mode: FileScrollMode.Infinite);

        browser.PageSize.Should().Be(250);
        browser.IsInfiniteScroll.Should().BeTrue();
        browser.VisibleFileCount.Should().Be(250);
    }

    [Fact]
    public void Construction_NullSettings_Throws()
    {
        Action act = () => _ = new FileBrowser(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
