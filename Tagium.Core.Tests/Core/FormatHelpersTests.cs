using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Tagium.Core;

namespace Tagium.Core.Tests.Core;

public class FormatHelpersTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(5242880, "5 MB")]
    [InlineData(3489660928, "3.25 GB")]
    [InlineData(1099511627776, "1 TB")]
    public void FormatSize_FormatsBytes(long bytes, string expected)
    {
        FormatHelpers.FormatSize(bytes).Should().Be(expected);
    }

    [Fact]
    public void FormatUnix_HasDateTimeShape()
    {
        string formatted = FormatHelpers.FormatUnix(1767225600);

        Regex.IsMatch(formatted, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$").Should().BeTrue();
    }

    [Fact]
    public void FormatUnix_RoundTripsThroughLocalTime()
    {
        const long unix = 1767225600; // 2026-01-01 00:00:00 UTC

        string formatted = FormatHelpers.FormatUnix(unix);
        DateTime parsed = DateTime.ParseExact(formatted, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        new DateTimeOffset(parsed).ToUnixTimeSeconds().Should().Be(unix);
    }
}
