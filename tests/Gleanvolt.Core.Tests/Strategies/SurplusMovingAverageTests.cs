using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class SurplusMovingAverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SingleSample_AveragesToItself()
    {
        var average = new SurplusMovingAverage(TimeSpan.FromMinutes(3));

        Assert.Equal(4000, average.Add(Start, 4000));
    }

    [Fact]
    public void AveragesSamplesInsideTheWindow()
    {
        var average = new SurplusMovingAverage(TimeSpan.FromMinutes(3));

        average.Add(Start, 4000);
        average.Add(Start.AddSeconds(30), 2000);
        var result = average.Add(Start.AddSeconds(60), 3000);

        Assert.Equal(3000, result); // (4000 + 2000 + 3000) / 3
        Assert.Equal(3, average.Count);
    }

    [Fact]
    public void SamplesOlderThanTheWindowAreEvicted()
    {
        var average = new SurplusMovingAverage(TimeSpan.FromMinutes(3));

        average.Add(Start, 10000);                       // will age out
        var result = average.Add(Start.AddMinutes(4), 1000);

        Assert.Equal(1000, result);
        Assert.Equal(1, average.Count);
    }

    [Fact]
    public void BriefDipDoesNotDragTheAverageBelowTheFloor()
    {
        // The point of the smoothing: a single 15-second cloud must not end a charging session.
        // Ten samples at 4000W then one at 0W still averages well above the ~1380W 6A floor.
        var average = new SurplusMovingAverage(TimeSpan.FromMinutes(3));
        var now = Start;

        double result = 0;
        for (var i = 0; i < 10; i++)
        {
            result = average.Add(now, 4000);
            now = now.AddSeconds(15);
        }

        result = average.Add(now, 0); // the cloud

        Assert.True(result > 3600, $"expected the average to stay high, was {result}");
    }

    [Fact]
    public void SustainedDrop_EventuallyPullsTheAverageDown()
    {
        var average = new SurplusMovingAverage(TimeSpan.FromMinutes(3));
        var now = Start;

        for (var i = 0; i < 12; i++)
        {
            average.Add(now, 4000);
            now = now.AddSeconds(15);
        }

        // Cloud stays for a full window: every 4000W sample ages out.
        double result = 0;
        for (var i = 0; i < 13; i++)
        {
            result = average.Add(now, 0);
            now = now.AddSeconds(15);
        }

        Assert.Equal(0, result);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var average = new SurplusMovingAverage(TimeSpan.FromMinutes(3));
        average.Add(Start, 5000);

        average.Reset();

        Assert.Equal(0, average.Count);
        Assert.Equal(1000, average.Add(Start.AddSeconds(15), 1000));
    }

    [Fact]
    public void NonPositiveWindow_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SurplusMovingAverage(TimeSpan.Zero));
}
