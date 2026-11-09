using Gleanvolt.Devices;

namespace Gleanvolt.Tests;

/// <summary>
/// What the reference installation's SolaX charger reads in its use-mode register, as these tests describe
/// it. The controllers no longer know these (issue #348): they read <see cref="EvChargerSettings.ChargingAllowed"/>,
/// which the SolaX driver sets for Fast only. The tests keep the charger's own words because Eco and Green
/// are the owner's modes, and "left alone in Green" says what a test is about better than "not allowed".
/// </summary>
public enum SolaxMode
{
    Stop = 0,
    Fast = 1,
    Eco = 2,
    Green = 3,
}

public static class SolaxModeSettings
{
    /// <summary>The settings the SolaX driver reports for <paramref name="mode"/> at <paramref name="amps"/>.</summary>
    public static EvChargerSettings At(this SolaxMode mode, int amps) =>
        new(mode == SolaxMode.Fast, amps, mode.ToString());

    /// <summary>The use-mode the SolaX driver writes for a start (Fast) or a stop (Stop).</summary>
    public static SolaxMode Written(bool allowed) => allowed ? SolaxMode.Fast : SolaxMode.Stop;
}
