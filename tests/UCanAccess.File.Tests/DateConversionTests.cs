using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

public class DateConversionTests
{
    [Theory]
    [InlineData(0.0, 1899, 12, 30, 0, 0, 0)]
    [InlineData(1.0, 1899, 12, 31, 0, 0, 0)]
    [InlineData(2.0, 1900, 1, 1, 0, 0, 0)]
    [InlineData(-1.0, 1899, 12, 29, 0, 0, 0)]
    [InlineData(0.5, 1899, 12, 30, 12, 0, 0)]
    [InlineData(-0.5, 1899, 12, 30, 12, 0, 0)]
    [InlineData(25569.0, 1970, 1, 1, 0, 0, 0)]
    [InlineData(41424.0, 2013, 5, 30, 0, 0, 0)]
    public void OADate_converts_like_jackcess(double oa, int y, int mo, int d, int h, int mi, int s)
    {
        DateTime expected = new(y, mo, d, h, mi, s);
        DateTime actual = Column.LdtFromLocalDateDouble(oa);
        Assert.Equal(expected, new DateTime(actual.Year, actual.Month, actual.Day, actual.Hour, actual.Minute, actual.Second));
    }

    [Fact]
    public void Fractional_dates_round_to_milliseconds()
    {
        // fraction 0.5543287037037037 of a day = 13:18:14 exactly (1/300s Access precision)
        double oa = 41424.0 + 47894.0 / 86400.0;
        DateTime dt = Column.LdtFromLocalDateDouble(oa);
        Assert.Equal(new DateTime(2013, 5, 30, 13, 18, 14), dt);
        Assert.Equal(0, dt.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void Subsecond_fraction_rounds_to_milliseconds()
    {
        // 13:18:14.976 -> rounds to whole ms, seconds component stays 14
        DateTime dt = Column.LdtFromLocalDateDouble(41424.554340);
        Assert.Equal(new DateTime(2013, 5, 30, 13, 18, 14), new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second));
        Assert.Equal(0, dt.Ticks % TimeSpan.TicksPerMillisecond);
    }
}
