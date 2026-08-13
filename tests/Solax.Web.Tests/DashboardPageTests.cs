using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solax.Core.Enums;
using Solax.Core.Models;
using Solax.Web.Components.Pages;

namespace Solax.Web.Tests;

/// <summary>
/// Phase 1 (#46): a read-only view of live telemetry, with no control affordance anywhere on the
/// page. Follows the same holder-subscription seam <see cref="HealthPageTests"/> already covers, so
/// these tests focus on what phase 1 adds: the fields themselves, and that nothing here can be
/// clicked to change anything.
/// </summary>
public class DashboardPageTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);

    public DashboardPageTests()
    {
        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
    }

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = Render<Dashboard>();

        Assert.Contains("No poll has completed yet", page.Markup);
    }

    [Fact]
    public void Shows_the_core_telemetry_fields()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 3200,
            SurplusWatts = 1800,
            BatterySocPercent = 72,
            BatteryPowerWatts = 450,
            GridPowerWatts = -300,
            EvChargerPowerWatts = 2760,
            EvChargingCurrentAmps = 12,
            TargetCurrentAmps = 12,
            ActiveCurrentAmps = 12,
            ChargerStatus = EvChargerStatus.Charging,
            CarConnected = true,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Solar", page.Markup);
        Assert.Contains("3200 W", page.Markup);
        Assert.Contains("1800 W", page.Markup);
        Assert.Contains("72%", page.Markup);
        Assert.Contains("450 W", page.Markup);
        Assert.Contains("-300 W", page.Markup);
        Assert.Contains("2760 W", page.Markup);
        Assert.Contains("12 A", page.Markup);
        Assert.Contains("Charging", page.Markup);
        Assert.Contains("Yes", page.Markup);
    }

    [Fact]
    public void Blanks_the_surplus_and_current_fields_when_the_mode_isnt_deciding_on_them()
    {
        _holder.Set(Statuses.Sample(_time.Now) with
        {
            SurplusWatts = null,
            TargetCurrentAmps = null,
            ActiveCurrentAmps = null,
        });

        var page = Render<Dashboard>();

        Assert.Contains("—", page.Markup);
    }

    [Fact]
    public void Follows_the_holder_instead_of_sampling_it_once()
    {
        var page = Render<Dashboard>();
        Assert.Contains("No poll has completed yet", page.Markup);

        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted));

        page.WaitForAssertion(() => Assert.Contains("Forecasted", page.Markup));
    }

    [Fact]
    public void Stops_following_the_holder_once_the_circuit_is_gone()
    {
        var page = Render<Dashboard>();
        page.Dispose();

        var exception = Record.Exception(() => _holder.Set(Statuses.Sample(_time.Now)));

        Assert.Null(exception);
    }

    [Fact]
    public void Never_renders_a_form_input_button_or_select()
    {
        // The whole point of phase 1: telemetry only, nothing that could change what the charger is
        // doing. A control creeping in here would be a phase-3 (#48) feature landing before its own
        // issue, and before authentication (#47) gates it.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted));

        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("input"));
        Assert.Empty(page.FindAll("button"));
        Assert.Empty(page.FindAll("select"));
        Assert.Empty(page.FindAll("form"));
    }
}
