using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

/// <summary>
/// Whether a reading is fit to convert a percentage target from (issue #179).
///
/// <para>The distinction the whole type exists for is <b>absent versus stale</b>. They look alike from
/// the arithmetic's side — neither yields a number — and they want opposite things from an owner: one
/// a feed, or asking in kilowatt-hours for good; the other the portal button pressed and the same
/// request again. A guard that folded them together would tell an install with no car at all that its
/// reading was too old.</para>
/// </summary>
public class VehicleSocBasisTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private static VehicleState Reported(TimeSpan ago, double? soc = 64) =>
        new(Now - ago, SocPercent: soc);

    [Fact]
    public void A_reading_inside_max_age_is_convertible()
    {
        var basis = VehicleSocBasis.From(Reported(TimeSpan.FromHours(5)), MaxAge, Now);

        Assert.False(basis.IsStale);
        Assert.Equal(64, basis.ConvertibleSocPercent);
        Assert.Null(basis.StaleRefusal);
    }

    /// <summary>
    /// The measured case. #141 clocked the reference portal at a median 5 h 10 m old on arrival, so
    /// 13 h is not a contrived figure — it is a Tuesday.
    /// </summary>
    [Fact]
    public void A_reading_past_max_age_is_stale_and_refuses_with_both_figures()
    {
        var basis = VehicleSocBasis.From(Reported(TimeSpan.FromHours(13)), MaxAge, Now);

        Assert.True(basis.IsStale);
        Assert.Null(basis.ConvertibleSocPercent);

        var refusal = basis.StaleRefusal;
        Assert.NotNull(refusal);

        // Both halves, because either alone is unactionable: the age says the reading is old, MaxAge
        // says what this install treats as current, and the setting name says where to change it.
        Assert.Contains("64%", refusal);
        Assert.Contains("13.0 h", refusal);
        Assert.Contains("12.0 h", refusal);
        Assert.Contains("Vehicle:MaxAge", refusal);
        Assert.Contains("kilowatt-hours", refusal);
    }

    /// <summary>
    /// The line the issue draws. A car with no feed is a fully supported installation (#137) and this
    /// guard must be invisible to it — <see cref="VehicleSocBasis.None"/> has nothing to be stale
    /// about, and the factories refuse it in the words it deserves instead.
    /// </summary>
    [Fact]
    public void No_reading_at_all_is_not_stale()
    {
        Assert.False(VehicleSocBasis.None.IsStale);
        Assert.Null(VehicleSocBasis.None.StaleRefusal);
        Assert.Null(VehicleSocBasis.None.ConvertibleSocPercent);

        // And the same for a feed that is configured but has never delivered.
        var never = VehicleSocBasis.From(null, MaxAge, Now);
        Assert.False(never.IsStale);
        Assert.Null(never.StaleRefusal);
    }

    /// <summary>
    /// A feed can carry range and a plug state and no percentage at all — the ID.4 over
    /// <c>volkswagen_connect</c> reports no target SOC, and an OBD dongle reports SOC alone. Age is
    /// beside the point when there is no figure to age.
    /// </summary>
    [Fact]
    public void A_reading_carrying_no_state_of_charge_is_not_stale_whatever_its_age()
    {
        var basis = VehicleSocBasis.From(
            new VehicleState(Now - TimeSpan.FromDays(3), RangeKm: 312, PlugState: VehiclePlugState.Connected),
            MaxAge,
            Now);

        Assert.False(basis.IsStale);
        Assert.Null(basis.StaleRefusal);
    }

    /// <summary>
    /// A car whose clock runs ahead of ours produces a negative age. Fresh, not an error — the same
    /// rule <see cref="VehicleState.IsStaleAt"/> already follows, and the two must not disagree.
    /// </summary>
    [Fact]
    public void A_clock_running_ahead_of_ours_is_fresh_rather_than_an_error()
    {
        var basis = VehicleSocBasis.From(Reported(TimeSpan.FromMinutes(-20)), MaxAge, Now);

        Assert.False(basis.IsStale);
        Assert.Equal(64, basis.ConvertibleSocPercent);
    }

    /// <summary>Exactly at the limit is still current: the guard fires past MaxAge, not at it.</summary>
    [Fact]
    public void A_reading_exactly_at_max_age_is_still_convertible()
    {
        Assert.False(VehicleSocBasis.From(Reported(MaxAge), MaxAge, Now).IsStale);
        Assert.True(VehicleSocBasis.From(Reported(MaxAge + TimeSpan.FromSeconds(1)), MaxAge, Now).IsStale);
    }

    /// <summary>
    /// The age is measured from the <b>car's</b> capture time, not from when the reading reached us.
    /// That is the whole finding of #141: the portal's bundles arrived hours after the car made them.
    /// </summary>
    [Fact]
    public void The_age_comes_from_the_cars_own_capture_time()
    {
        var basis = VehicleSocBasis.From(Reported(TimeSpan.FromHours(2)), MaxAge, Now);

        Assert.Equal(TimeSpan.FromHours(2), basis.Age);
    }

    /// <summary>The worst case #141 measured, phrased in days rather than in 70 hours.</summary>
    [Fact]
    public void A_reading_days_old_is_described_in_days()
    {
        var basis = VehicleSocBasis.From(Reported(TimeSpan.FromHours(70)), MaxAge, Now);

        Assert.Contains("2.9 days", basis.StaleRefusal);
    }
}
