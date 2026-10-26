using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The owner's <b>until full</b> option for <see cref="ChargeControlMode.Forecasted"/> (issue #279): with
/// it on, one Forecasted session runs over several days. Where the mode would end itself for the day
/// (<see cref="ChargingSessionEndReason.NoSunLeftToday"/>) it stands the charger down instead and starts
/// again on the next day's sun, until the car is full or someone presses Off.
///
/// <para>Seeded from configuration and settable at runtime from the web UI, Home Assistant and the API,
/// like <see cref="ILowTariffSettings"/>. Unlike it, a session running with the option on survives a
/// restart: the host keeps it on disk while it lasts (decision 1 of #279).</para>
/// </summary>
public interface IUntilFullSettings
{
    /// <summary>Whether Forecasted runs until the car is full rather than ending with the day's sun.</summary>
    bool UntilFull { get; }

    /// <summary>Sets the option. <paramref name="source"/> names who changed it, for logging.</summary>
    void SetUntilFull(bool on, string source);
}
