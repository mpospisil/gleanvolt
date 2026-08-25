using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.HomeAssistant;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Phase 4 of #80: parity between the two surfaces. What the web page can do — set an amount, set a
/// departure, apply both — Home Assistant can do, and the plan it produces is reported the same way.
/// </summary>
public class HaTargetedEntityTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly HomeAssistantOptions Options = new()
    {
        BaseTopic = "solax",
        DeviceId = "solax_controller",
        DiscoveryPrefix = "homeassistant",
    };

    private static readonly HaDiscovery Discovery = new(Options, Sites.Home);

    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero); // 22:00 Prague

    [Theory]
    [InlineData("homeassistant/number/solax_controller/target_energy/config")]
    [InlineData("homeassistant/text/solax_controller/target_departure/config")]
    [InlineData("homeassistant/button/solax_controller/activate_target/config")]
    [InlineData("homeassistant/sensor/solax_controller/target_plan_state/config")]
    [InlineData("homeassistant/sensor/solax_controller/target_solar/config")]
    [InlineData("homeassistant/sensor/solax_controller/target_grid/config")]
    [InlineData("homeassistant/sensor/solax_controller/target_expected/config")]
    [InlineData("homeassistant/sensor/solax_controller/target_shortfall/config")]
    [InlineData("homeassistant/sensor/solax_controller/target_grid_start/config")]
    public void EveryTargetedEntityIsDiscovered(string topic) =>
        Assert.Contains(Discovery.DiscoveryMessages(), m => m.Topic == topic);

    [Fact]
    public void TheDepartureTextCarriesItsOwnCommandAndStateTopics()
    {
        var message = Discovery.DiscoveryMessages()
            .Single(m => m.Topic == "homeassistant/text/solax_controller/target_departure/config");

        using var json = JsonDocument.Parse(message.Payload);
        var config = json.RootElement;

        Assert.Equal("solax/home-roof/target_departure/set", config.GetProperty("command_topic").GetString());
        Assert.Equal("solax/home-roof/target_departure/state", config.GetProperty("state_topic").GetString());
        Assert.Equal("Departure time", config.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("")]                    // no departure requested -- the state this box is usually in
    [InlineData("07:00")]
    [InlineData("7:00")]
    [InlineData("2026-08-11 07:00")]
    [InlineData("2026-08-11T07:00")]
    public void ThePatternAdmitsEveryStateTheBoxCanBeIn(string value)
    {
        // Issue #117: the empty one is the point. Home Assistant validates the state it receives against
        // this pattern, and the controller publishes an empty departure whenever there is no target --
        // on connect, and every time a targeted session ends. A pattern that could not express that
        // turned a normal state into an ERROR in somebody's log, several times a day.
        Assert.Matches(Pattern(), value);
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("07")]
    [InlineData("07:00 please")]
    [InlineData(" ")]
    public void ThePatternStillKeepsTheObviousTyposOut(string value)
    {
        Assert.DoesNotMatch(Pattern(), value);
    }

    private static string Pattern()
    {
        var message = Discovery.DiscoveryMessages()
            .Single(m => m.Topic == "homeassistant/text/solax_controller/target_departure/config");

        using var json = JsonDocument.Parse(message.Payload);
        return json.RootElement.GetProperty("pattern").GetString()!;
    }

    [Fact]
    public void TheActivateButtonTakesOnlyAPress()
    {
        var message = Discovery.DiscoveryMessages()
            .Single(m => m.Topic == "homeassistant/button/solax_controller/activate_target/config");

        using var json = JsonDocument.Parse(message.Payload);

        Assert.Equal(Discovery.ActivateTargetCommandTopic, json.RootElement.GetProperty("command_topic").GetString());
        Assert.Equal(HaDiscovery.PayloadPress, json.RootElement.GetProperty("payload_press").GetString());
    }

    [Fact]
    public void TheStatePayloadCarriesTheTargetBlockWhileTheModeIsDriving()
    {
        var plan = Plan();
        using var json = JsonDocument.Parse(Discovery.StateJson(Status(ChargeControlMode.Targeted, plan)));
        var state = json.RootElement;

        Assert.Equal(plan.Reason, state.GetProperty("target_reason").GetString());
        Assert.Equal(14.6, state.GetProperty("target_forecast_surplus_kwh").GetDouble(), 1);
        Assert.Equal(14.6, state.GetProperty("target_solar_kwh").GetDouble(), 1);
        Assert.Equal(7.4, state.GetProperty("target_grid_kwh").GetDouble(), 1);
        Assert.Equal(22.0, state.GetProperty("target_expected_kwh").GetDouble(), 1);
        Assert.Equal(0, state.GetProperty("target_shortfall_kwh").GetDouble(), 1);
        Assert.True(state.TryGetProperty("target_grid_start", out _));
    }

    [Fact]
    public void TheForecastSurplusIsPublishedBesideTheSolarShare()
    {
        // The pair is the point: a zero solar share against a real surplus is the weak-sun day, and a
        // dashboard showing only the share reports it as a dark one.
        var messages = Discovery.DiscoveryMessages().ToList();

        Assert.Contains(messages, m => m.Payload.Contains("Target forecast surplus"));
        Assert.Contains(messages, m => m.Payload.Contains("target_forecast_surplus_kwh"));
    }

    [Fact]
    public void TheTargetBlockIsAbsentWhenAnotherModeIsDriving()
    {
        // The same rule the forecast plan follows: no dashboard may show a target nothing is working to.
        using var json = JsonDocument.Parse(Discovery.StateJson(Status(ChargeControlMode.Solar, plan: null)));

        Assert.False(json.RootElement.TryGetProperty("target_reason", out _));
        Assert.False(json.RootElement.TryGetProperty("target_grid_kwh", out _));
    }

    [Fact]
    public void ABareTimeMeansTheNextOccurrenceOfIt()
    {
        Assert.True(HaDiscovery.TryParseDeparture("07:00", Now, Prague, out var departure));

        // 22:00 Prague on the 10th: "07:00" is tomorrow morning, which is 05:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 5, 0, 0, TimeSpan.Zero), departure);
    }

    [Fact]
    public void ABareTimeLaterTodayStaysToday()
    {
        Assert.True(HaDiscovery.TryParseDeparture("23:30", Now, Prague, out var departure));

        Assert.Equal(new DateTimeOffset(2026, 8, 10, 21, 30, 0, TimeSpan.Zero), departure);
    }

    [Fact]
    public void AFullTimestampMeansExactlyItself()
    {
        Assert.True(HaDiscovery.TryParseDeparture("2026-08-13 06:45", Now, Prague, out var departure));

        Assert.Equal(new DateTimeOffset(2026, 8, 13, 4, 45, 0, TimeSpan.Zero), departure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tomorrow morning")]
    [InlineData("25:00")]
    [InlineData("7am")]
    [InlineData(null)]
    public void GarbageIsRefusedRatherThanGuessedAt(string? payload) =>
        Assert.False(HaDiscovery.TryParseDeparture(payload, Now, Prague, out _));

    private static TargetedChargePlan Plan() => new(
        Strategy: TargetedChargeStrategy.SolarPlusGrid,
        Now: Now,
        DepartBy: Now.AddHours(9),
        Deadline: Now.AddHours(9).AddMinutes(-15),
        RequiredEnergyWh: 22_000,
        DeliveredEnergyWh: 0,
        RemainingEnergyWh: 22_000,
        SolarEnergyWh: 14_600,
        ForecastSurplusWh: 14_600,
        RequiredPaceWatts: 3_000,
        GridEnergyWh: 7_400,
        CeilingEnergyWh: 90_000,
        ExpectedEnergyWh: 22_000,
        ShortfallWh: 0,
        GridStart: Now.AddHours(6.5),
        FeasibleDeparture: null,
        SocFloorPercent: 55,
        BatteryToFullWh: 2_000,
        Blocks: [],
        ForecastAsOf: Now,
        IsUsable: true,
        TailEnergyWh: 0,
        HoldUntil: null,
        Reason: "22.0kWh by Tue 07:00: 14.6kWh from sun, 7.4kWh from the grid starting 04:30.");

    private static ChargeControlStatus Status(ChargeControlMode mode, TargetedChargePlan? plan) =>
        new(mode, DryRun: true, HoldingControl: true, ChargeControlState.Charging, SurplusWatts: null,
            TargetCurrentAmps: 16, ActiveCurrentAmps: 16, BatterySocPercent: 60,
            ChargerStatus: EvChargerStatus.Charging, CarConnected: true, SolarPowerWatts: 0,
            ForecastSolarPowerWatts: 0, EvChargerPowerWatts: 11_040, EvChargingCurrentAmps: 16,
            BatteryPowerWatts: 0, GridPowerWatts: 11_340, BatteryHoldEnabled: true,
            BatteryHoldRequested: false, BatteryHoldActive: true, BatteryHoldTargetWatts: -11_040,
            Plan: null, LoanPowerWatts: 0, SessionEnergyWh: 0, LoanedTodayWh: 0, TomorrowForecastWh: null,
            Timestamp: Now, TargetedPlan: plan);

    // --- Just in time (#101) ---

    [Fact]
    public void ThePriorityIsPublishedAsASelectWithBothChoices()
    {
        var (_, payload) = Assert.Single(
            Discovery.DiscoveryMessages(),
            m => m.Topic == "homeassistant/select/solax_controller/target_priority/config");

        Assert.Contains("\"Cheapest\"", payload);
        Assert.Contains("\"JustInTime\"", payload);
        Assert.Contains("Charge priority", payload);
    }

    [Fact]
    public void TheRestSocIsPublishedAsASettableNumberAndSubscribedTo()
    {
        Assert.Contains(
            Discovery.DiscoveryMessages(),
            m => m.Topic == "homeassistant/number/solax_controller/target_rest_soc/config");

        Assert.Contains(HaDiscovery.TargetRestSocNumber, HaDiscovery.NumberObjectIds);
    }

    [Fact]
    public void TheHoldIsReportedAsATimeOrAsNone()
    {
        var held = Discovery.StateJson(Status(
            ChargeControlMode.Targeted,
            Plan() with { TailEnergyWh = 6_000, HoldUntil = Now.AddHours(8) }));

        Assert.Contains("target_hold_until", held);

        // "none" rather than absent: an unavailable entity reads the same as a dead feed, and nothing
        // being held is a perfectly good answer.
        var unheld = Discovery.StateJson(Status(ChargeControlMode.Targeted, Plan()));
        Assert.Contains("\"target_hold_until\":\"none\"", unheld);
    }
}
