namespace Gleanvolt.Core.Models;

/// <summary>
/// One field a feed recognised, as it arrived: the raw value, and when the car reported it.
///
/// <para><b>The time is the point.</b> A reading assembled from merged deliveries takes each field
/// from whichever report carried it, so two figures shown side by side can be hours apart — and
/// comparing them without that is how a coarse reading and a stale one get mistaken for each other.
/// A state of charge of 60 next to an energy content implying 69 is a contradiction if they share a
/// clock and simply two moments if they do not.</para>
/// </summary>
/// <param name="Value">Exactly what the source said, untouched — sentinels and units included.</param>
/// <param name="At">When the vehicle reported it.</param>
public sealed record VehicleFieldReading(string Value, DateTimeOffset At);
