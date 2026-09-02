using Gleanvolt.Core.Enums;

namespace Gleanvolt.Core.Models;

/// <summary>
/// Whether a vehicle feed can currently produce a reading, and why — the state for an automation to
/// key off and the sentence for a person to read (issue #140).
///
/// <para>The pair is the point. A state alone cannot say <i>which</i> screen the portal is showing,
/// and a sentence alone cannot be compared by a Home Assistant automation. Every surface here shows
/// both: the dashboard renders the sentence under the state's heading, and the <c>Car feed</c> entity
/// publishes the state with the sentence attached.</para>
/// </summary>
/// <param name="State">Which of the three situations the feed is in.</param>
/// <param name="Message">
/// One sentence, safe to render and safe to log: it names what happened and what would fix it, and
/// never carries a credential.
/// </param>
public sealed record VehicleSourceHealth(VehicleSourceState State, string Message)
{
    /// <summary>What a service reports before its first fetch has completed. Not an alarm.</summary>
    public static VehicleSourceHealth Starting { get; } =
        new(VehicleSourceState.Degraded, "waiting for the first reading");

    public static VehicleSourceHealth Ok(string message) => new(VehicleSourceState.Ok, message);

    public static VehicleSourceHealth Degraded(string message) => new(VehicleSourceState.Degraded, message);

    public static VehicleSourceHealth NeedsOwner(string message) => new(VehicleSourceState.NeedsOwner, message);

    /// <summary>Whether the owner has to do something before this feed can work again.</summary>
    public bool IsBlocked => State == VehicleSourceState.NeedsOwner;
}
