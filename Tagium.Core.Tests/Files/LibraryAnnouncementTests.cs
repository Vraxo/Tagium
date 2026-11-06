using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class LibraryAnnouncementTests
{
    [Theory]
    [InlineData(5, 12, "5 of 12 selected")]
    [InlineData(1, 1, "1 of 1 selected")]
    [InlineData(12, 12, "12 of 12 selected")]
    public void Selection_PartialOrComplete_ReportsCounts(int selected, int total, string expected)
    {
        LibraryAnnouncement.Selection(selected, total).Should().Be(expected);
    }

    [Fact]
    public void Selection_NothingSelected_StillReportsLibrarySize()
    {
        LibraryAnnouncement.Selection(0, 12).Should().Be("Nothing selected, 12 files");
    }

    [Fact]
    public void Selection_NothingSelectedFromSingleFile_UsesSingular()
    {
        LibraryAnnouncement.Selection(0, 1).Should().Be("Nothing selected, 1 file");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -5)]
    [InlineData(3, 0)]
    public void Selection_EmptyLibrary_ReportsNoFiles(int selected, int total)
    {
        LibraryAnnouncement.Selection(selected, total).Should().Be("No files");
    }

    [Fact]
    public void Selection_MoreSelectedThanExist_ClampsToTotal()
    {
        LibraryAnnouncement.Selection(20, 12).Should().Be("12 of 12 selected");
    }

    [Fact]
    public void Selection_NegativeSelection_ReadsAsNothingSelected()
    {
        LibraryAnnouncement.Selection(-2, 12).Should().Be("Nothing selected, 12 files");
    }

    [Theory]
    [InlineData(12, "12 files")]
    [InlineData(1, "1 file")]
    [InlineData(0, "No files")]
    [InlineData(-3, "No files")]
    public void Library_ReportsSizeWithCorrectPlural(int total, string expected)
    {
        LibraryAnnouncement.Library(total).Should().Be(expected);
    }
}
