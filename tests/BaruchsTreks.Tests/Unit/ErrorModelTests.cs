using System.Globalization;
using BaruchsTreks.Pages;

namespace BaruchsTreks.Tests.Unit;

public class ErrorModelTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("cs-CZ")]
    public void Time_IsInvariantRegardlessOfCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var model = new ErrorModel(new FixedClock(new DateTimeOffset(2026, 7, 1, 12, 5, 9, TimeSpan.Zero)));

            Assert.Equal("2026-07-01 12:05:09", model.Time);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
