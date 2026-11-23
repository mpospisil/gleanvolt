using Gleanvolt.Hosting.Chargers;
using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Gleanvolt words its refusals in the viewer's language; the contract's rules (#439) are what a plugin's tests
/// check with. Both must refuse the same readings, for the same reasons, or a plugin passes its tests and is
/// refused by Gleanvolt.
/// </summary>
public sealed class ChargerReadingRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, ChargerReading> Readings { get; } = new()
    {
        { "fine", new ChargerReading(Now, ChargerState.Charging, 6900, CurrentAmps: 10, ChargingAllowed: true, MaxCurrentAmps: 16, Mode: "Normal") },
        { "no time", new ChargerReading(default, ChargerState.Available, 0) },
        { "from the future", new ChargerReading(Now.AddMinutes(6), ChargerState.Available, 0) },
        { "too old", new ChargerReading(Now.AddMinutes(-2), ChargerState.Available, 0) },
        { "unknown status", new ChargerReading(Now, (ChargerState)42, 0) },
        { "negative power", new ChargerReading(Now, ChargerState.Charging, -1) },
        { "power in mW", new ChargerReading(Now, ChargerState.Charging, 6_900_000) },
        { "not a number", new ChargerReading(Now, ChargerState.Charging, double.NaN) },
        { "setpoint too high", new ChargerReading(Now, ChargerState.Charging, 0, CurrentAmps: 81) },
        { "ceiling too low", new ChargerReading(Now, ChargerState.Charging, 0, MaxCurrentAmps: 5) },
        { "mode too long", new ChargerReading(Now, ChargerState.Charging, 0, Mode: new string('x', 51)) },
        { "everything", new ChargerReading(Now.AddHours(-1), (ChargerState)42, -5, CurrentAmps: -1, MaxCurrentAmps: 99, Mode: new string('x', 60)) },
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public void Gleanvolt_and_the_contract_refuse_the_same_readings_in_the_same_words(string name, ChargerReading reading)
    {
        _ = name;

        var gleanvolt = ChargerReadingValidator.Check(reading, Now);
        var contract = ChargerReadingRules.Check(reading, Now);

        Assert.Equal(contract, [.. gleanvolt]);
    }
}
