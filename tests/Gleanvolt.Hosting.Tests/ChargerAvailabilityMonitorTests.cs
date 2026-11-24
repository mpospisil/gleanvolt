using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Hosting.Chargers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>What makes the charger unavailable (#441), and the grace period before it counts.</summary>
public sealed class ChargerAvailabilityMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);

    private readonly Clock _clock = new() { Now = Start };
    private ExternalProviderStatus? _plugin = Connected();

    [Fact]
    public void A_connected_healthy_plugin_whose_charger_answers_is_available()
    {
        Assert.Equal(ChargerAvailability.Ready, Observe(Read()));
    }

    [Fact]
    public void No_plugin_is_NoPlugin_once_the_grace_period_is_over()
    {
        _plugin = new ExternalProviderStatus("charger", "Charger", "/providers/charger", true, ExternalProviderState.Waiting);

        Assert.True(Observe(Read()).Available);

        _clock.Now = Start.AddMinutes(2);
        var after = Observe(Read());

        Assert.Equal(ChargerAvailabilityState.NoPlugin, after.State);
        Assert.Equal("No charger plugin is connected.", after.Reason);
        Assert.Equal(Start, after.Since);
    }

    [Fact]
    public void A_refused_plugin_needs_its_owner_with_the_refusal_as_the_reason()
    {
        _plugin = new ExternalProviderStatus("charger", "Charger", "/providers/charger", true, ExternalProviderState.Waiting,
            LastRefusal: new ExternalProviderRefusal("huawei", "Huawei 7KS", "limits",
                "ChargeControl:Phases (3) is more than the Huawei 7KS charger supplies (1).",
                [Phrase.Of("Config.ChargerPhases", new PhraseArg.Number(3), new PhraseArg.Text("Huawei 7KS"), new PhraseArg.Number(1))],
                Start));

        var availability = ObserveLong();

        Assert.Equal(ChargerAvailabilityState.NeedsOwner, availability.State);
        Assert.Contains("ChargeControl:Phases (3)", availability.Reason);
        Assert.Equal("Charger.PluginRefused", availability.ReasonPhrase!.Key);
    }

    [Theory]
    [InlineData(true, ChargerAvailabilityState.NeedsOwner)]
    [InlineData(false, ChargerAvailabilityState.Error)]
    public void A_plugin_reporting_a_problem_is_unavailable_with_what_it_said(bool needsOwner, ChargerAvailabilityState expected)
    {
        _plugin = Connected() with
        {
            Healthy = false,
            NeedsOwner = needsOwner,
            StatusMessage = "No address yet.",
        };

        var availability = ObserveLong();

        Assert.Equal(expected, availability.State);
        Assert.Contains("No address yet.", availability.Reason);
    }

    [Fact]
    public void A_charger_that_cant_be_read_is_an_Error_whatever_drives_it()
    {
        _plugin = null;

        var availability = ObserveLong("Connection refused");

        Assert.Equal(ChargerAvailabilityState.Error, availability.State);
        Assert.Equal("Gleanvolt can't read the charger: Connection refused", availability.Reason);
    }

    [Fact]
    public void One_good_read_ends_the_outage_and_the_next_one_starts_its_own_grace_period()
    {
        _plugin = null;
        Observe(Read("timeout"));
        _clock.Now = Start.AddMinutes(1).AddSeconds(50);
        Observe(Read());
        var changes = 0;
        _monitor.Changed += () => changes++;

        _clock.Now = Start.AddMinutes(2).AddSeconds(10);
        Assert.True(Observe(Read("timeout")).Available);

        _clock.Now = Start.AddMinutes(4).AddSeconds(10);
        Assert.False(Observe(Read("timeout")).Available);
        Assert.Equal(1, changes);
    }

    private readonly ChargerAvailabilityMonitor _monitor;

    public ChargerAvailabilityMonitorTests()
    {
        _monitor = new ChargerAvailabilityMonitor(
            _clock, TimeSpan.FromMinutes(2), () => _plugin, NullLogger<ChargerAvailabilityMonitor>.Instance);
    }

    private ChargerAvailability Observe(EnergyState state) => _monitor.Observe(state);

    // Unavailable from the start and still so three minutes later.
    private ChargerAvailability ObserveLong(string? error = null)
    {
        Observe(Read(error));
        _clock.Now = Start.AddMinutes(3);
        return Observe(Read(error));
    }

    private EnergyState Read(string? error = null) =>
        new(_clock.Now, 60, 0, 0, 0, error is null ? EvChargerStatus.Available : EvChargerStatus.Unknown, 0, EvChargerReadError: error);

    private static ExternalProviderStatus Connected() =>
        new("charger", "Charger", "/providers/charger", true, ExternalProviderState.Connected,
            ProviderId: "shelly-topac", ProviderName: "Shelly TopAC", Since: Start, Healthy: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
