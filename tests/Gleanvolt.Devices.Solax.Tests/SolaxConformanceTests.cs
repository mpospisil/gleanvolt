using Gleanvolt.Devices.Testing;

namespace Gleanvolt.Devices.Solax.Tests;

/// <summary>
/// The SolaX driver against the conformance tests every driver is held to (issue #319).
///
/// <para>The fixtures under Fixtures/ are <b>synthetic</b> for now: written from the reference installation's
/// figures, and labelled so in their <c>note</c>. Replace each with a Settings → "Save register snapshot" of
/// the reference installation in the state its name describes; the tests do not change.</para>
/// </summary>
public class SolaxConformanceTests : InverterDriverConformanceTests<SolaxInverterDriverFactory>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    protected override SolaxInverterDriverFactory CreateFactory() => new();

    protected override RegisterDump NightImporting => Load(Fixture("solax-night-importing.json"));

    protected override RegisterDump BatteryCharging => Load(Fixture("solax-battery-charging.json"));

    protected override RegisterDump OtherFamily => Load(Fixture("solax-ev-charger.json"));
}
