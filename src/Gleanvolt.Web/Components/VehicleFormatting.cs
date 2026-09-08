using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The car's own figures, formatted once (issue #178).
///
/// <para>Shared by the dashboard's vehicle card and <c>/vehicle-portal</c>, and not as tidiness: the
/// portal page exists to be held up against the card, and two copies of "how old is this reading"
/// that round differently would have the two pages disagreeing over a reading they agree on.</para>
///
/// <para>Coarse on purpose throughout. This data is routinely hours old, so precision past the first
/// hour would claim an accuracy the source does not have — and a dash is never a zero: absent and
/// zero are different facts everywhere the car is concerned.</para>
/// </summary>
internal static class VehicleFormatting
{
    /// <summary>A state of charge, or a dash when the source did not carry one.</summary>
    public static string Percent(double? percent) => percent is null ? "—" : $"{percent.Value:F0}%";

    /// <summary>
    /// The range the car reckons is left in it. Whole kilometres: it is the car's own estimate off
    /// its own recent consumption, and a decimal on it would be borrowed confidence.
    /// </summary>
    public static string Km(double? km) => km is { } value ? $"{value:F0} km" : "—";

    /// <summary>
    /// The car's own estimate of how much longer it needs. Null and zero are different facts (#73):
    /// zero means the car says it is done, a dash means this source does not carry the field at all.
    /// </summary>
    public static string ChargeTime(VehicleState reading) =>
        reading.ChargeTimeRemaining is { } left ? $"{left.TotalMinutes:F0} min" : "—";

    /// <summary>
    /// How long ago the <b>car</b> produced this reading, as of <paramref name="now"/> — not how long
    /// ago we received it. A negative age (the car's clock running ahead of ours) reads as "just now"
    /// rather than as a negative duration.
    /// </summary>
    public static string Age(VehicleState vehicle, DateTimeOffset now)
    {
        var age = vehicle.AgeAt(now);

        if (age < TimeSpan.Zero)
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{age.TotalMinutes:F0} min";
        }

        return age < TimeSpan.FromDays(1)
            ? $"{age.TotalHours:F1} h"
            : $"{age.TotalDays:F1} days";
    }
}
