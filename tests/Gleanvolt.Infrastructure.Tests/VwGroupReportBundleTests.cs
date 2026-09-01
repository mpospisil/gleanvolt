using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// Reading a download into snapshots (issue #139): both layouts, several snapshots per delivery, and
/// the things a bundle carries that are not readings.
///
/// <para>No network anywhere — bytes in, snapshots out. That split is what makes the hard half of this
/// feature testable at all, and it is the same discipline <see cref="VehicleTelemetryPayloadTests"/>
/// follows.</para>
/// </summary>
public class VwGroupReportBundleTests
{
    [Fact]
    public void ReadsTheDottedIdxLayout()
    {
        Assert.True(VwGroupReportBundle.TryRead(
            VwGroupFixtures.Bundle("meb-dotted.json"), out var snapshots, out var error));

        Assert.Null(error);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("54", snapshots[0].Values["battery.stateOfChargeInPercent"]);
        Assert.Equal("61", snapshots[1].Values["battery.stateOfChargeInPercent"]);
    }

    [Fact]
    public void ReadsTheFlatPhevLayoutIncludingItsUnquotedNumbers()
    {
        // The older export sends the same field as a JSON number where the newer one quotes it.
        // Everything is normalised to a string, because one representation downstream is worth more
        // than preserving that distinction.
        Assert.True(VwGroupReportBundle.TryRead(
            VwGroupFixtures.Bundle("phev-flat.json"), out var snapshots, out _));

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("37", snapshot.Values["stateOfChargeInPercent"]);
        Assert.Equal("92140", snapshot.Values["mileage"]);
    }

    [Fact]
    public void ReturnsSnapshotsOldestFirstAcrossTheWholeDelivery()
    {
        // "Last occurrence wins" downstream is only meaningful if the order is guaranteed here.
        Assert.True(VwGroupReportBundle.TryRead(
            VwGroupFixtures.Bundle("meb-dotted.json", "phev-flat.json", "sentinels-and-partials.json"),
            out var snapshots, out _));

        Assert.Equal(
            snapshots.Select(snapshot => snapshot.CapturedAt).OrderBy(at => at),
            snapshots.Select(snapshot => snapshot.CapturedAt));
    }

    [Fact]
    public void KeepsTheDatasetsOwnTimestampAndItsOffset()
    {
        // Never the download time: substituting ours would make every stale reading look fresh, which
        // is the one failure this feed exists to make visible.
        Assert.True(VwGroupReportBundle.TryRead(
            VwGroupFixtures.Bundle("meb-dotted.json"), out var snapshots, out _));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 31, 21, 14, 7, TimeSpan.FromHours(2)), snapshots[0].CapturedAt);
        Assert.Equal(TimeSpan.FromHours(2), snapshots[0].CapturedAt.Offset);
    }

    [Fact]
    public void DropsAnUndatedSnapshotRatherThanDatingIt()
    {
        Assert.True(VwGroupReportBundle.TryRead(
            VwGroupFixtures.Bundle("meb-dotted.json", "undated.json"), out var snapshots, out _));

        Assert.Equal(2, snapshots.Count);
        Assert.DoesNotContain(snapshots, snapshot => snapshot.Values.Values.Contains("99"));
    }

    [Fact]
    public void ABundleOfNothingDatedIsRejectedWithAReason()
    {
        Assert.False(VwGroupReportBundle.TryRead(
            VwGroupFixtures.Bundle("undated.json"), out var snapshots, out var error));

        Assert.Empty(snapshots);
        Assert.Contains("dated", error);
    }

    [Fact]
    public void OneUnreadableMemberDoesNotCostTheDelivery()
    {
        // A bundle carries several snapshots precisely so that any of them can be the good one.
        Assert.True(VwGroupReportBundle.TryRead(
            VwGroupFixtures.BundleWithBrokenMember("phev-flat.json"), out var snapshots, out _));

        Assert.Single(snapshots);
    }

    [Fact]
    public void SomethingThatIsNotAZipSaysSo()
    {
        // Almost always a bounce to /login or an error page arriving where a ZIP was expected. Saying
        // that beats a stack trace about a bad central directory.
        Assert.False(VwGroupReportBundle.TryRead(
            "<html><body>Sign in</body></html>"u8.ToArray(), out _, out var error));

        Assert.Contains("not a ZIP", error);
    }

    [Fact]
    public void AnEmptyDownloadIsRejectedRatherThanParsed()
    {
        Assert.False(VwGroupReportBundle.TryRead([], out _, out var error));
        Assert.Contains("empty", error);
    }
}
