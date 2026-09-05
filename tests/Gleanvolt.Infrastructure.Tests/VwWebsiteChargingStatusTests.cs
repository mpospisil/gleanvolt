using Gleanvolt.Core.Enums;
using Gleanvolt.Infrastructure.Vehicles.VwWebsite;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The live source's payload (issue #170), against a real capture from the reference ID.4.
///
/// <para>The capture is why this client exists. Taken 2026-09-05, it carries a state of charge the
/// car recorded at 08:39 that morning — while the EU Data Act portal, asked within the minute, was
/// still serving one captured at 21:16 the evening before. Same car, same fact, eleven and a half
/// hours apart.</para>
/// </summary>
public class VwWebsiteChargingStatusTests
{
    private static string Capture() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VwWebsite", "charging-status.json"));

    [Fact]
    public void The_real_payload_maps_to_a_reading()
    {
        var state = VwWebsiteChargingStatus.Parse(Capture(), "vw-website", out var error);

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal(55, state!.SocPercent);
        Assert.Equal(220, state.RangeKm);
        Assert.Equal("vw-website", state.SourceId);
    }

    /// <summary>The reason for the whole client: the car's own capture time, and a recent one.</summary>
    [Fact]
    public void The_capture_time_is_the_cars_own()
    {
        var state = VwWebsiteChargingStatus.Parse(Capture(), null, out _);

        Assert.Equal(new DateTimeOffset(2026, 9, 5, 8, 39, 1, TimeSpan.Zero), state!.CapturedAt);
    }

    [Fact]
    public void The_apps_vocabulary_is_understood()
    {
        var state = VwWebsiteChargingStatus.Parse(Capture(), null, out _);

        // "notReadyForCharging" here, where the Data Act bundle says CHARGE_STATE_NOT_READY_FOR_CHARGING.
        Assert.Equal(VehicleChargeState.Idle, state!.ChargeState);
        Assert.Equal(VehiclePlugState.Disconnected, state.PlugState);
    }

    /// <summary>
    /// Null in this capture, while the Data Act portal carried settings.target_soc = 80 for the same
    /// car at the same moment. Neither source is a superset, which is why both are kept.
    /// </summary>
    [Fact]
    public void The_target_soc_is_absent_here_and_that_is_expected()
    {
        Assert.Null(VwWebsiteChargingStatus.TargetSocPercent(Capture()));
    }

    [Theory]
    [InlineData("charging", VehicleChargeState.Charging)]
    [InlineData("readyForCharging", VehicleChargeState.Idle)]
    [InlineData("chargePurposeReachedAndNotConservationCharging", VehicleChargeState.Complete)]
    [InlineData("somethingNew", VehicleChargeState.Unknown)]
    public void Charge_states_map_and_an_unknown_one_costs_only_itself(string value, VehicleChargeState expected)
    {
        var json =
            "{\"data\":{\"batteryStatus\":{\"carCapturedTimestamp\":\"2026-05-09T08:39:01Z\","
            + "\"currentSOC_pct\":55},\"chargingStatus\":{\"chargingState\":\"" + value + "\"}}}";

        var state = VwWebsiteChargingStatus.Parse(json, null, out var error);

        Assert.Null(error);
        Assert.Equal(expected, state!.ChargeState);
        Assert.Equal(55, state.SocPercent);
    }

    /// <summary>#73's rule: a body we cannot believe is refused whole rather than half-trusted.</summary>
    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("not json", "not JSON")]
    [InlineData("""{"nothing":1}""", "no 'data'")]
    [InlineData("""{"data":{"batteryStatus":{"currentSOC_pct":55}}}""", "carCapturedTimestamp")]
    public void An_unusable_body_is_refused_with_a_reason(string? json, string expected)
    {
        Assert.Null(VwWebsiteChargingStatus.Parse(json, null, out var error));
        Assert.Contains(expected, error);
    }

    /// <summary>Zero is the car saying it is finished, which is not the same as not saying.</summary>
    [Fact]
    public void A_zero_remaining_time_is_kept_as_a_fact()
    {
        var state = VwWebsiteChargingStatus.Parse(Capture(), null, out _);

        Assert.Equal(TimeSpan.Zero, state!.ChargeTimeRemaining);
    }
}
