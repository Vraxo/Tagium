using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class SupportedFormatsTests
{
    [Theory]
    [InlineData(".jpg", MediaKind.Image)]
    [InlineData(".JPEG", MediaKind.Image)]
    [InlineData(".png", MediaKind.Image)]
    [InlineData(".webp", MediaKind.Image)]
    [InlineData(".mp4", MediaKind.Video)]
    [InlineData(".MKV", MediaKind.Video)]
    [InlineData(".webm", MediaKind.Video)]
    [InlineData(".txt", MediaKind.Other)]
    [InlineData(".zip", MediaKind.Other)]
    [InlineData("", MediaKind.Other)]
    [InlineData(null, MediaKind.Other)]
    public void GetKind_ClassifiesExtension(string? extension, MediaKind expected)
    {
        SupportedFormats.GetKind(extension).Should().Be(expected);
    }

    [Fact]
    public void All_ContainsEveryImageAndVideoExtension()
    {
        SupportedFormats.All.Should().HaveCount(SupportedFormats.ImageExtensions.Count + SupportedFormats.VideoExtensions.Count);

        foreach (string ext in SupportedFormats.ImageExtensions)
        {
            SupportedFormats.All.Should().Contain(ext);
        }
        foreach (string ext in SupportedFormats.VideoExtensions)
        {
            SupportedFormats.All.Should().Contain(ext);
        }
    }

    [Fact]
    public void All_IsCaseInsensitive()
    {
        SupportedFormats.All.Contains(".JPG").Should().BeTrue();
        SupportedFormats.All.Contains(".Mp4").Should().BeTrue();
    }
}
