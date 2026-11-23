namespace Gleanvolt.Core.Models;

/// <summary>Whether Gleanvolt can control the charger (#441), in the four states Home Assistant shows.</summary>
public enum ChargerAvailabilityState
{
    /// <summary>The charger answers and its plugin, if any, is connected and well.</summary>
    Available,

    /// <summary>The charger is behind a plugin and none is connected.</summary>
    NoPlugin,

    /// <summary>Only the owner can fix it: a plugin refused for its limits or for another being remembered, or one asking for its owner.</summary>
    NeedsOwner,

    /// <summary>The charger can't be read, or its plugin reports it isn't working.</summary>
    Error,
}

/// <summary>
/// Whether Gleanvolt can control the charger (#441), and if not, why and since when. While it can't, no mode is
/// started, and a running one is suspended: kept, with nothing commanded and the battery hold released, until the
/// charger is back. A shorter outage than <c>ChargeControl:ChargerUnavailableAfter</c> reads as available.
/// </summary>
/// <param name="State">Which.</param>
/// <param name="Reason">Why not, in English, for the log, the API and Home Assistant. Null when available.</param>
/// <param name="ReasonPhrase">The same for the web UI, or null to show the English.</param>
/// <param name="Since">When the charger became unavailable, the grace period included. Null when available.</param>
public sealed record ChargerAvailability(
    ChargerAvailabilityState State,
    string? Reason = null,
    Phrase? ReasonPhrase = null,
    DateTimeOffset? Since = null)
{
    /// <summary>The charger can be controlled.</summary>
    public static ChargerAvailability Ready { get; } = new(ChargerAvailabilityState.Available);

    /// <summary>Whether it can be controlled.</summary>
    public bool Available => State == ChargerAvailabilityState.Available;
}
