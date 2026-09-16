using Gleanvolt.Core.Enums;
using Gleanvolt.Infrastructure.Vehicles.Skoda;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The MyŠkoda Public API's vehicle response mapped onto a reading (issue #193), against fixtures built
/// from the published spec. See <c>Fixtures/Skoda/README.md</c>: none of these is a capture from a car
/// yet, and the <c>state</c> mapping is the part most likely to be corrected by one.
/// </summary>
public class SkodaVehicleResponseTests
{
    [Fact]
    public void A_charging_car_maps_to_a_reading()
    {
        var state = SkodaVehicleResponse.Parse(SkodaFixtures.Read("charging.json"), "skoda", out var error);

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal(71, state!.SocPercent);
        Assert.Equal(249, state.RangeKm);
        Assert.Equal(TimeSpan.FromMinutes(95), state.ChargeTimeRemaining);
        Assert.Equal(VehicleChargeState.Charging, state.ChargeState);
        Assert.Equal(VehiclePlugState.Connected, state.PlugState);
        Assert.Equal("skoda", state.SourceId);
    }

    /// <summary>The age of a reading is the car's, from the charging part, and never "now".</summary>
    [Fact]
    public void The_capture_time_is_the_cars_own()
    {
        var state = SkodaVehicleResponse.Parse(SkodaFixtures.Read("charging.json"), null, out _);

        Assert.Equal(new DateTimeOffset(2026, 9, 15, 18, 35, 0, TimeSpan.Zero), state!.CapturedAt);
    }

    /// <summary>The API has no plug field; the car asking for a cable is what says there is none.</summary>
    [Fact]
    public void Connect_cable_means_unplugged()
    {
        var state = SkodaVehicleResponse.Parse(SkodaFixtures.Read("connect-cable.json"), null, out _);

        Assert.Equal(48, state!.SocPercent);
        Assert.Equal(VehicleChargeState.Idle, state.ChargeState);
        Assert.Equal(VehiclePlugState.Disconnected, state.PlugState);
        Assert.Null(state.ChargeTimeRemaining);
    }

    [Theory]
    [InlineData("CHARGING", VehicleChargeState.Charging, VehiclePlugState.Connected)]
    [InlineData("CONSERVING", VehicleChargeState.Complete, VehiclePlugState.Connected)]
    [InlineData("READY_FOR_CHARGING", VehicleChargeState.Idle, VehiclePlugState.Connected)]
    [InlineData("CHARGING_INTERRUPTED", VehicleChargeState.Idle, VehiclePlugState.Connected)]
    [InlineData("CONNECT_CABLE", VehicleChargeState.Idle, VehiclePlugState.Disconnected)]
    [InlineData("DISCHARGING", VehicleChargeState.Unknown, VehiclePlugState.Unknown)]
    [InlineData("SOMETHING_NEW", VehicleChargeState.Unknown, VehiclePlugState.Unknown)]
    [InlineData(null, VehicleChargeState.Unknown, VehiclePlugState.Unknown)]
    public void The_state_table_from_the_issue(string? value, VehicleChargeState charge, VehiclePlugState plug)
    {
        // "Clients must tolerate values they do not recognise": a new state costs its own field, never
        // the state of charge beside it.
        Assert.Equal(charge, SkodaVehicleResponse.ChargeState(value));
        Assert.Equal(plug, SkodaVehicleResponse.PlugState(value));
    }

    /// <summary>A 200 with the charging part missing is partial data, and not a reading.</summary>
    [Fact]
    public void Partial_data_without_charging_is_refused_with_the_reason_the_api_gave()
    {
        var state = SkodaVehicleResponse.Parse(SkodaFixtures.Read("charging-unavailable.json"), null, out var error);

        Assert.Null(state);
        Assert.Contains("CHARGING_UNAVAILABLE", error);
    }

    [Fact]
    public void A_reading_without_a_capture_time_is_refused()
    {
        var json = SkodaFixtures.Read("charging.json").Replace("\"carCapturedTimestamp\"", "\"somethingElse\"");

        Assert.Null(SkodaVehicleResponse.Parse(json, null, out var error));
        Assert.Contains("carCapturedTimestamp", error);
    }

    [Fact]
    public void A_percentage_outside_0_to_100_refuses_the_reading_whole()
    {
        var json = SkodaFixtures.Read("charging.json").Replace("\"stateOfChargeInPercent\": 71", "\"stateOfChargeInPercent\": 710");

        Assert.Null(SkodaVehicleResponse.Parse(json, null, out var error));
        Assert.Contains("stateOfChargeInPercent", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"errors\": []}")]
    public void A_body_that_is_not_a_vehicle_is_refused(string json)
    {
        Assert.Null(SkodaVehicleResponse.Parse(json, null, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_cars_name_comes_from_the_info_part()
    {
        Assert.Equal("My Enyaq", SkodaVehicleResponse.Name(SkodaFixtures.Read("info.json")));
    }
}
