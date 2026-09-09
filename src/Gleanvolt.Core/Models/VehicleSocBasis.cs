namespace Gleanvolt.Core.Models;

/// <summary>
/// The reading a "charge it to 80%" may be converted from — and whether it is fit to be converted
/// from at all (issue #179).
///
/// <para><b>Why this is not just a <c>double?</c>.</b> It used to be. A percentage on its own cannot
/// answer the only question that matters at the moment of conversion: <i>is this still true?</i>
/// <c>Vehicle:MaxAge</c> greyed a number on a card, and nothing stopped a plan turning an
/// hours-old percentage into the kilowatt-hours a charger would then deliver against. #141 measured
/// the reference portal at a <b>median 5 h 10 m old on arrival</b>, worst case 2 d 22 h.</para>
///
/// <para>Through the two-feed comparison week that was survivable, because the newest of two feeds
/// won and one of them was usually recent. That is no longer true: for most of the day the portal is
/// the only thing reporting, and there is no second opinion left to correct a stale reading.</para>
///
/// <para><b>Absent is not stale.</b> A car with no feed at all is a fully supported installation
/// (#137) and this changes nothing for it — <see cref="None"/> is not stale, it simply has no
/// percentage, and the factories already refuse that in its own words. The guard fires on
/// <i>configured and old</i>, never on <i>absent</i>.</para>
///
/// <para><b>Still advisory.</b> Nothing here gates charging. It refuses to compute an amount from a
/// number it cannot vouch for, and an owner asking in kilowatt-hours is unaffected — which is the
/// whole fallback: "I cannot tell you what 80% is in kWh right now" beats silently spending
/// yesterday's percentage.</para>
/// </summary>
/// <param name="SocPercent">What the car last reported, or null when nothing has.</param>
/// <param name="Age">
/// How old that reading was when this basis was taken. Null when there is no reading. Negative is
/// possible — a car's clock can run ahead of ours — and counts as fresh rather than as an error.
/// </param>
/// <param name="MaxAge">
/// How old a reading may be and still be converted from, <c>Vehicle:MaxAge</c>. Carried here rather
/// than on <see cref="VehiclePackLimits"/> because it describes the <b>feed</b> and the pack figures
/// describe the <b>car</b> — two configuration sections, and the split is deliberate.
/// </param>
public sealed record VehicleSocBasis(
    double? SocPercent = null,
    TimeSpan? Age = null,
    TimeSpan MaxAge = default)
{
    /// <summary>
    /// No reading to convert from: no feed configured, or one that has never reported. Explicitly
    /// <b>not</b> stale — there is nothing to be stale about.
    /// </summary>
    public static VehicleSocBasis None { get; } = new();

    /// <summary>
    /// The basis a surface takes from whatever its feed is holding, at the moment it is asked.
    /// </summary>
    /// <param name="reading">The feed's current state, or null when it has none.</param>
    /// <param name="maxAge">The installation's <c>Vehicle:MaxAge</c>.</param>
    /// <param name="now">The instant the request is being made.</param>
    public static VehicleSocBasis From(VehicleState? reading, TimeSpan maxAge, DateTimeOffset now) =>
        reading is null ? None : new(reading.SocPercent, reading.AgeAt(now), maxAge);

    /// <summary>
    /// A reading that exists and is older than <see cref="MaxAge"/>.
    ///
    /// <para>Requires a percentage as well as an age: a feed delivering range and plug state but no
    /// state of charge has nothing to convert from whatever its age, and that is the missing-reading
    /// refusal rather than this one.</para>
    /// </summary>
    public bool IsStale => SocPercent is not null && Age is { } age && age > MaxAge;

    /// <summary>
    /// The percentage a conversion may honestly stand on, or null when it may not — which folds
    /// "never reported" and "reported too long ago" into the one answer arithmetic can take.
    ///
    /// <para>Callers that mean to tell the two apart must ask <see cref="IsStale"/> <b>first</b>: they
    /// need opposite things from an owner. One wants a feed, or asking in kilowatt-hours for good; the
    /// other wants the button on <c>/vehicle-portal</c> pressed, and then the same request again.</para>
    /// </summary>
    public double? ConvertibleSocPercent => IsStale ? null : SocPercent;

    /// <summary>
    /// The refusal, in the terms it was asked in and with both figures in it, or null when the reading
    /// is fit to convert from. Shared by both factories so the three doors cannot word it differently.
    /// </summary>
    public string? StaleRefusal =>
        IsStale
            ? $"The car last reported {SocPercent:F0}% {Describe(Age!.Value)} ago, past the "
                + $"{Describe(MaxAge)} this installation treats as current (Vehicle:MaxAge), so there is "
                + "no honest way to say what that target is in kilowatt-hours. Read the car again, or "
                + "ask in kilowatt-hours instead."
            : null;

    /// <summary>
    /// A duration in words, coarse on purpose: this one is measured in hours and days, and a reading
    /// "5 h" old says everything that "5 h 11 m 04 s" would.
    /// </summary>
    private static string Describe(TimeSpan span) =>
        span < TimeSpan.FromHours(1)
            ? $"{span.TotalMinutes:F0} min"
            : span < TimeSpan.FromDays(1)
                ? $"{span.TotalHours:F1} h"
                : $"{span.TotalDays:F1} days";
}
