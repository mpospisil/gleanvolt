using Solax.Worker.Configuration;

namespace Solax.Worker.Tests;

public class ZonedTimeProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithNoZone_KeepsTheSystemProvider(string? configured) =>
        Assert.Same(TimeProvider.System, ZonedTimeProvider.Resolve(configured));

    [Fact]
    public void Resolve_WithAnIanaId_ReportsThatZone()
    {
        var provider = ZonedTimeProvider.Resolve("Europe/Prague");

        // The point of the setting: the zone comes from configuration, not from the host, so a
        // Windows container that ignores TZ still computes the right day boundary.
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"), provider.LocalTimeZone);
    }

    [Fact]
    public void Resolve_IgnoresSurroundingWhitespace() =>
        Assert.Equal(
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"),
            ZonedTimeProvider.Resolve("  Europe/Prague  ").LocalTimeZone);

    [Fact]
    public void Resolve_WithAnUnknownId_Throws() =>
        // Deliberately fatal rather than a fall back to UTC: a silent shift of the day boundary
        // would only surface as bad charging decisions days later.
        Assert.Throws<TimeZoneNotFoundException>(() => ZonedTimeProvider.Resolve("Europe/Nowhere"));

    [Fact]
    public void PassesTheClockThroughUntouched()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var provider = new ZonedTimeProvider(zone);

        var before = TimeProvider.System.GetUtcNow();
        var observed = provider.GetUtcNow();
        var after = TimeProvider.System.GetUtcNow();

        Assert.InRange(observed, before, after);
        Assert.Equal(TimeProvider.System.TimestampFrequency, provider.TimestampFrequency);
    }
}
