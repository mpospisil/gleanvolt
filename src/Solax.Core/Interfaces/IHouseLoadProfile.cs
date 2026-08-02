namespace Solax.Core.Interfaces;

/// <summary>
/// What the house is expected to be drawing (EV excluded) at a given moment. The day plan needs this
/// to know how much of the forecast is spoken for before either battery sees it.
///
/// <para>It is a <em>profile</em> rather than a single number for a reason found in validation: a
/// household's load has a strong daily shape, and a trailing average of it is always wrong in the same
/// direction — at 15:00 it reports the afternoon peak, which then gets projected across the evening
/// and wipes out the car's budget. Asking per-instant lets the estimate say "3 kW now, 400 W at
/// 22:00" instead of "3 kW for the rest of the day".</para>
/// </summary>
public interface IHouseLoadProfile
{
    /// <summary>Expected household load in watts at <paramref name="instant"/>, EV charging excluded.</summary>
    double ExpectedWattsAt(DateTimeOffset instant);
}
