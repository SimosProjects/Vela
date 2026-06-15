namespace Vela.Tests.Unit;

/// <summary>
/// Tests for the market day and market hours logic used by MarketSchedulerService
/// and BrokerExecutionService. Extracted as pure static logic tests since the
/// scheduler itself runs as a BackgroundService and is hard to unit test directly.
/// </summary>
public class MarketHoursTests
{
    // The logic under test mirrors IsMarketDay in MarketSchedulerService
    // and IsMarketOpen in BrokerExecutionService.

    private static bool IsMarketDay(DayOfWeek day, int month, int dayOfMonth)
    {
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        // Fixed US market holidays
        if ((month == 1  && dayOfMonth == 1)  ||  // New Year's Day
            (month == 6  && dayOfMonth == 19) ||  // Juneteenth
            (month == 7  && dayOfMonth == 4)  ||  // Independence Day
            (month == 12 && dayOfMonth == 25))     // Christmas Day
            return false;

        return true;
    }

    private static bool IsMarketOpen(TimeSpan timeOfDay, DayOfWeek day)
    {
        if (day is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        return timeOfDay >= new TimeSpan(9, 30, 0)
            && timeOfDay <  new TimeSpan(16, 0, 0);
    }

    // -- IsMarketDay: weekday tests --

    [Theory]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Tuesday)]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Thursday)]
    [InlineData(DayOfWeek.Friday)]
    public void IsMarketDay_Weekday_ReturnsTrue(DayOfWeek day)
    {
        IsMarketDay(day, 5, 15).Should().BeTrue();
    }

    [Theory]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    public void IsMarketDay_Weekend_ReturnsFalse(DayOfWeek day)
    {
        IsMarketDay(day, 5, 15).Should().BeFalse();
    }

    // -- IsMarketDay: fixed holiday tests --

    [Theory]
    [InlineData(1,  1,  "New Year's Day")]
    [InlineData(6,  19, "Juneteenth")]
    [InlineData(7,  4,  "Independence Day")]
    [InlineData(12, 25, "Christmas Day")]
    public void IsMarketDay_FixedHoliday_ReturnsFalse(int month, int day, string holiday)
    {
        // Use Wednesday to eliminate weekend effect
        IsMarketDay(DayOfWeek.Wednesday, month, day)
            .Should().BeFalse(because: $"{holiday} is a market holiday");
    }

    [Fact]
    public void IsMarketDay_DayBeforeChristmas_ReturnsTrue()
    {
        // Dec 24 is not a fixed holiday
        IsMarketDay(DayOfWeek.Wednesday, 12, 24).Should().BeTrue();
    }

    [Fact]
    public void IsMarketDay_DayAfterNewYear_ReturnsTrue()
    {
        // Jan 2 is not a fixed holiday
        IsMarketDay(DayOfWeek.Wednesday, 1, 2).Should().BeTrue();
    }

    // -- IsMarketOpen: time boundary tests --

    [Fact]
    public void IsMarketOpen_At930_ReturnsTrue()
    {
        // Market opens at exactly 9:30am
        IsMarketOpen(new TimeSpan(9, 30, 0), DayOfWeek.Monday).Should().BeTrue();
    }

    [Fact]
    public void IsMarketOpen_At929_ReturnsFalse()
    {
        // One minute before open
        IsMarketOpen(new TimeSpan(9, 29, 59), DayOfWeek.Monday).Should().BeFalse();
    }

    [Fact]
    public void IsMarketOpen_At1559_ReturnsTrue()
    {
        // One second before close
        IsMarketOpen(new TimeSpan(15, 59, 59), DayOfWeek.Monday).Should().BeTrue();
    }

    [Fact]
    public void IsMarketOpen_At1600_ReturnsFalse()
    {
        // Market closes at exactly 4:00pm (exclusive)
        IsMarketOpen(new TimeSpan(16, 0, 0), DayOfWeek.Monday).Should().BeFalse();
    }

    [Fact]
    public void IsMarketOpen_AtNoon_ReturnsTrue()
    {
        IsMarketOpen(new TimeSpan(12, 0, 0), DayOfWeek.Wednesday).Should().BeTrue();
    }

    [Fact]
    public void IsMarketOpen_OnSaturday_ReturnsFalse()
    {
        // Even during normal market hours, weekends are closed
        IsMarketOpen(new TimeSpan(12, 0, 0), DayOfWeek.Saturday).Should().BeFalse();
    }

    [Fact]
    public void IsMarketOpen_OnSunday_ReturnsFalse()
    {
        IsMarketOpen(new TimeSpan(12, 0, 0), DayOfWeek.Sunday).Should().BeFalse();
    }

    [Fact]
    public void IsMarketOpen_PreMarket_ReturnsFalse()
    {
        IsMarketOpen(new TimeSpan(8, 0, 0), DayOfWeek.Monday).Should().BeFalse();
    }

    [Fact]
    public void IsMarketOpen_AfterHours_ReturnsFalse()
    {
        IsMarketOpen(new TimeSpan(17, 0, 0), DayOfWeek.Monday).Should().BeFalse();
    }
}
