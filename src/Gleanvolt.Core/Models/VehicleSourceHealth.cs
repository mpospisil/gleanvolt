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
public sealed record VehicleSourceHealth(VehicleSourceState State, string Message, Phrase? Phrase = null)
{
    /// <summary>What a service reports before its first fetch has completed. Not an alarm.</summary>
    public static VehicleSourceHealth Starting { get; } =
        new(VehicleSourceState.Degraded, "waiting for the first reading", Phrase.Of("Health.Starting"));

    public static VehicleSourceHealth Ok(string message, Phrase? phrase = null) => new(VehicleSourceState.Ok, message, phrase);

    public static VehicleSourceHealth Degraded(string message, Phrase? phrase = null) => new(VehicleSourceState.Degraded, message, phrase);

    public static VehicleSourceHealth NeedsOwner(string message, Phrase? phrase = null) => new(VehicleSourceState.NeedsOwner, message, phrase);

    /// <summary>Whether the owner has to do something before this feed can work again.</summary>
    public bool IsBlocked => State == VehicleSourceState.NeedsOwner;

    // The phrase restates the message for the web UI (#292); two healths that say the same thing are
    // the same health, which is what the feed alert's "did anything change?" relies on.
    public bool Equals(VehicleSourceHealth? other) =>
        other is not null && State == other.State && Message == other.Message;

    public override int GetHashCode() => HashCode.Combine(State, Message);
}
