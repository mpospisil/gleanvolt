using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.HomeAssistant;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The tariff in Home Assistant (issue #265): <b>Low tariff</b>, <b>Tariff change</b> and <b>Grid
/// price</b>, published whatever the provider so they exist before a tariff does, and reading unknown —
/// never "high" or zero — while there is none.
/// </summary>
public class HaTariffEntityTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly HomeAssistantOptions Options = new()
    {
        BaseTopic = "solax",
        DeviceId = "solax_controller",
        DiscoveryPrefix = "homeassistant",
    };

    private static readonly HaDiscovery Discovery = new(Options, Sites.Home, tariffCurrency: "EUR");

    // 23:00 on a Wednesday in Prague: low tariff, until 06:00.
    private static readonly DateTimeOffset LateEvening = new(2026, 10, 21, 23, 0, 0, TimeSpan.FromHours(2));

    private static readonly TariffTimeline Night = TariffExpansion.Expand(
        new TariffTable([new("00:00", "06:00"), new("22:00", "24:00")], []),
        Prague,
        new DateOnly(2026, 10, 21),
        new TariffPrices(2.10m, 4.80m, "EUR"));

    private static ChargeControlStatus Status(DateTimeOffset at) =>
        new(ChargeControlMode.Off, DryRun: false, HoldingControl: false, ChargeControlState.Disabled, SurplusWatts: null,
            TargetCurrentAmps: null, ActiveCurrentAmps: null, BatterySocPercent: 50,
            ChargerStatus: EvChargerStatus.Available, CarConnected: false, SolarPowerWatts: 0,
            ForecastSolarPowerWatts: 0, EvChargerPowerWatts: 0, EvChargingCurrentAmps: 0, BatteryPowerWatts: 0,
            GridPowerWatts: 300, BatteryHoldEnabled: false, BatteryHoldRequested: false, BatteryHoldActive: false,
            BatteryHoldTargetWatts: null, Plan: null, LoanPowerWatts: 0, SessionEnergyWh: 0, LoanedTodayWh: 0,
            TomorrowForecastWh: null, Timestamp: at);

    private static JsonElement Config(string component, string objectId)
    {
        var (_, payload) = Assert.Single(
            Discovery.DiscoveryMessages(),
            m => m.Topic == $"homeassistant/{component}/solax_controller/{objectId}/config");
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static JsonElement State(TariffTimeline? tariff, DateTimeOffset at) =>
        JsonDocument.Parse(Discovery.StateJson(Status(at), tariff: tariff)).RootElement.Clone();

    [Fact]
    public void AllThreeArePublishedWithNoTariffAtAll()
    {
        Assert.Equal("Low tariff", Config("binary_sensor", "low_tariff").GetProperty("name").GetString());
        Assert.Equal("timestamp", Config("sensor", "tariff_change").GetProperty("device_class").GetString());
        Assert.Equal("EUR/kWh", Config("sensor", "grid_price").GetProperty("unit_of_measurement").GetString());
    }

    [Fact]
    public void TheGridPriceIsARateNotAnAmountOfMoney()
    {
        var price = Config("sensor", "grid_price");

        Assert.False(price.TryGetProperty("device_class", out _));
        Assert.Equal("measurement", price.GetProperty("state_class").GetString());
    }

    [Fact]
    public void WithNoTariffTheFieldsAreAbsentSoTheEntitiesReadUnknown()
    {
        var state = State(null, LateEvening);

        Assert.False(state.TryGetProperty("low_tariff", out _));
        Assert.False(state.TryGetProperty("tariff_change", out _));
        Assert.False(state.TryGetProperty("grid_price", out _));

        // The binary sensor's template turns an absent field into HA's "no value", not OFF.
        Assert.Contains(
            "default('None')",
            Config("binary_sensor", "low_tariff").GetProperty("value_template").GetString());
    }

    [Fact]
    public void InLowTariffItSaysSoWithThePriceAndTheNextChange()
    {
        var state = State(Night, LateEvening);

        Assert.Equal("ON", state.GetProperty("low_tariff").GetString());
        Assert.Equal(2.10m, state.GetProperty("grid_price").GetDecimal());
        Assert.Equal(
            new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.FromHours(2)),
            DateTimeOffset.Parse(state.GetProperty("tariff_change").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void InHighTariffItSaysSoToo()
    {
        var state = State(Night, LateEvening.AddHours(-11));

        Assert.Equal("OFF", state.GetProperty("low_tariff").GetString());
        Assert.Equal(4.80m, state.GetProperty("grid_price").GetDecimal());
    }

    [Fact]
    public void WithoutPricesThePriceIsAbsentButTheRateIsNot()
    {
        var state = State(Night with { Prices = null }, LateEvening);

        Assert.Equal("ON", state.GetProperty("low_tariff").GetString());
        Assert.False(state.TryGetProperty("grid_price", out _));
    }

    [Fact]
    public void AChangeBeyondWhatTheProviderKnowsIsAbsent()
    {
        // Tomorrow night's low runs past the end of the timeline, so no change is known.
        var state = State(Night, LateEvening.AddDays(1));

        Assert.Equal("ON", state.GetProperty("low_tariff").GetString());
        Assert.False(state.TryGetProperty("tariff_change", out _));
    }
}
