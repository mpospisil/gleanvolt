using Gleanvolt.Core.Enums;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// A real bundle, downloaded from the portal for the reference ID.4 and sanitised. The first fixture
/// here that is not synthetic, and the one that settles what the others could only guess at.
///
/// <para>It earned its place by contradicting four assumptions at once. The state of charge is not
/// <c>hv_soc</c> (documented, but this car does not send it) — it is
/// <c>battery_level_HV.value</c>. The odometer is <c>mileage.value</c>, not <c>mileage</c>. The
/// remaining charging time is <c>"9900s"</c>, seconds with the unit stuck to the digits, where the
/// dictionary documents minutes — and a plain numeric parse rejects that, which under #73's rule
/// throws away the entire bundle. And there is no range field at all: absent, not zero.</para>
/// </summary>
public class VwGroupLiveCaptureTests
{
    private const string Capture = "id4-live-capture.json";

    private static VwGroupMappingResult Map()
    {
        Assert.True(VwGroupReportBundle.TryRead(VwGroupFixtures.Bundle(Capture), out var snapshots, out var error), error);
        return VwGroupVehicleStateMapper.Map(snapshots, "id4");
    }

    [Fact]
    public void The_real_bundle_maps_to_a_usable_reading()
    {
        var result = Map();

        Assert.Null(result.Error);
        Assert.NotNull(result.State);
    }

    [Fact]
    public void The_state_of_charge_comes_from_battery_level_HV()
    {
        Assert.Equal(57, Map().State!.SocPercent);
    }

    /// <summary>The field #101 deferred its impossible-target gate for, present and readable.</summary>
    [Fact]
    public void The_cars_own_charge_limit_arrives()
    {
        Assert.Equal(80, Map().TargetSocPercent);
    }

    [Fact]
    public void The_charge_state_vocabulary_is_understood()
    {
        // CHARGE_STATE_NOT_READY_FOR_CHARGING -- a parked car with nothing plugged in.
        Assert.NotEqual(VehicleChargeState.Unknown, Map().State!.ChargeState);
    }

    /// <summary>Seconds, not minutes: 9900s is 165 minutes and must not read as 9900.</summary>
    [Fact]
    public void The_remaining_time_is_read_in_the_unit_it_names()
    {
        var left = Map().State!.ChargeTimeRemaining;

        Assert.NotNull(left);
        Assert.Equal(165, left!.Value.TotalMinutes, 1);
    }

    [Fact]
    public void The_odometer_comes_from_mileage_value()
    {
        Assert.Equal(53029, Map().OdometerKm);
    }

    /// <summary>Absent is absent. This bundle carries no range, and inventing zero would be a lie.</summary>
    [Fact]
    public void A_field_the_car_does_not_send_stays_null()
    {
        Assert.Null(Map().State!.RangeKm);
    }

    [Fact]
    public void The_capture_time_is_the_cars_own_clock()
    {
        // car_captured_time, not the moment we downloaded it.
        Assert.Equal(2026, Map().State!.CapturedAt.Year);
        Assert.NotEqual(default, Map().State!.CapturedAt);
    }
}
