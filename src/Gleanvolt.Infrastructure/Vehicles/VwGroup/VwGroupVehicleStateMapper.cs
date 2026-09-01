using System.Globalization;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// What a bundle of snapshots reduced to, and what was left over.
/// </summary>
/// <param name="State">The reading, or null when nothing usable came out.</param>
/// <param name="Error">Why not, when <paramref name="State"/> is null.</param>
/// <param name="TargetSocPercent">
/// The car's own charging target, if the portal carried one. Deliberately outside
/// <see cref="VehicleState"/>: #101's impossible-target gate stays deferred, and this exists so that
/// "does it actually arrive for this car?" is answerable without a code change.
/// </param>
/// <param name="OdometerKm">The odometer, for the same reason — and it is the monotonic tie-break's witness.</param>
/// <param name="UnmappedFields">
/// Field names the bundle carried that nothing here recognised.
///
/// <para>Reported rather than dropped silently, because the portal's vocabulary is not ours and was
/// written down from a description rather than from a capture. This list is how the first real
/// download tells you what <see cref="VwGroupFieldNames"/> is missing, in one glance instead of a
/// week of wondering why the SOC is null.</para>
/// </param>
public sealed record VwGroupMappingResult(
    VehicleState? State,
    string? Error = null,
    double? TargetSocPercent = null,
    double? OdometerKm = null,
    IReadOnlyList<string>? UnmappedFields = null)
{
    public IReadOnlyList<string> UnmappedFields { get; init; } = UnmappedFields ?? [];
}

/// <summary>
/// Turns the snapshots in one download into a single <see cref="VehicleState"/> (issue #139, step 5).
///
/// <para>Pure and static, tested against committed fixtures. This is where the actual work of #139
/// is: a download holds several snapshots, so <b>"take the last row" is wrong</b>, and three rules
/// decide what a field is worth.</para>
///
/// <list type="number">
/// <item><description><b>Sentinel filtering.</b> A blank, a <c>null</c>, an <c>unavailable</c> is not
/// a reading. Without this the newest snapshot wins by being newest even when it says nothing, and a
/// real value from an hour earlier is thrown away.</description></item>
/// <item><description><b>Largest wins for monotonic fields.</b> An odometer cannot go backwards, so a
/// smaller later value is a partial snapshot rather than news.</description></item>
/// <item><description><b>Last occurrence otherwise.</b> Snapshots arrive oldest-first, so the newest
/// non-sentinel value is the reading — which is the right default for everything that genuinely does
/// move both ways, SOC first among them.</description></item>
/// </list>
///
/// <para>#73's rules carry over unrenegotiated. <b>Absent is fine, present-but-unusable is not</b>: a
/// bundle whose SOC is <c>"error"</c> is rejected whole, so the holder keeps its last good reading and
/// its age visibly grows — a diagnosable state, rather than half-trusted junk. The one exception is an
/// <b>unrecognised enum value</b>, which maps to <c>Unknown</c>, because an unfamiliar charge state
/// must not cost us the state of charge.</para>
/// </summary>
public static class VwGroupVehicleStateMapper
{
    /// <summary>Sanity ceiling on a range, matching <see cref="VehicleTelemetryPayload"/>'s.</summary>
    private const double MaxRangeKm = 2000;

    /// <summary>Sanity ceiling on a remaining time: a week, matching <see cref="VehicleTelemetryPayload"/>'s.</summary>
    private const double MaxChargeTimeRemainingMinutes = 7 * 24 * 60;

    /// <summary>
    /// Reduces a download's snapshots to one reading.
    /// </summary>
    /// <param name="snapshots">Oldest first, as <see cref="VwGroupReportBundle"/> returns them.</param>
    /// <param name="sourceId">What to label the reading with, for display and diagnostics.</param>
    public static VwGroupMappingResult Map(IReadOnlyList<VwGroupSnapshot> snapshots, string? sourceId = null)
    {
        if (snapshots.Count == 0)
        {
            return new VwGroupMappingResult(null, "the bundle held no snapshots");
        }

        var ordered = snapshots.OrderBy(snapshot => snapshot.CapturedAt).ToList();

        // CapturedAt is the newest snapshot's own timestamp, offset intact -- never the download
        // time. A reading assembled from several snapshots is dated by the newest that contributed,
        // which is the most pessimistic honest answer: it is what the age is judged against.
        var capturedAt = ordered[^1].CapturedAt;

        if (!TryNumber(ordered, VwGroupFieldNames.StateOfCharge, 0, 100, out var soc, out var error))
        {
            return new VwGroupMappingResult(null, $"state of charge {error}");
        }

        if (!TryNumber(ordered, VwGroupFieldNames.RangeKm, 0, double.MaxValue, out var range, out error))
        {
            return new VwGroupMappingResult(null, $"range {error}");
        }

        range = NormaliseRange(ordered, range);

        if (range is > MaxRangeKm)
        {
            // The ceiling catches a unit the field name did not admit to, which is the one mistake
            // that would otherwise be displayed rather than caught.
            return new VwGroupMappingResult(null, $"range was {range} km, beyond {MaxRangeKm} km");
        }

        if (!TryNumber(
                ordered, VwGroupFieldNames.ChargeTimeRemainingMinutes,
                0, MaxChargeTimeRemainingMinutes, out var minutes, out error))
        {
            return new VwGroupMappingResult(null, $"remaining charging time {error}");
        }

        if (!TryNumber(ordered, VwGroupFieldNames.TargetSoc, 0, 100, out var targetSoc, out error))
        {
            return new VwGroupMappingResult(null, $"target state of charge {error}");
        }

        // Monotonic: the largest value across the bundle, not the last. An odometer that appears to
        // fall is a snapshot that was taken before the one before it, or one that was only partly
        // populated -- either way the bigger number is the true one.
        var odometer = Largest(ordered, VwGroupFieldNames.Odometer);

        var state = new VehicleState(
            capturedAt,
            soc,
            range,
            minutes is { } value ? TimeSpan.FromMinutes(value) : null,
            MapEnum(ordered, VwGroupFieldNames.ChargeState, VwGroupFieldNames.ChargeStates, VehicleChargeState.Unknown),
            MapEnum(ordered, VwGroupFieldNames.PlugState, VwGroupFieldNames.PlugStates, VehiclePlugState.Unknown),
            sourceId);

        return new VwGroupMappingResult(state, null, targetSoc, odometer, Unmapped(ordered));
    }

    /// <summary>
    /// The newest non-sentinel value of whichever field name matched, parsed as a number.
    ///
    /// <para>Returns false only for <b>present-but-unusable</b>: a value that is there, is not a
    /// sentinel, and still cannot be believed. Absent returns true with a null, which is a supported
    /// configuration rather than a fault.</para>
    /// </summary>
    private static bool TryNumber(
        List<VwGroupSnapshot> snapshots, string[] candidates,
        double minimum, double maximum, out double? number, out string? error)
    {
        number = null;
        error = null;

        if (Latest(snapshots, candidates) is not { } raw)
        {
            return true;
        }

        if (!double.TryParse(raw.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed))
        {
            error = $"was '{raw.Value}' on {raw.Field}, which is not a number";
            return false;
        }

        if (parsed < minimum || parsed > maximum)
        {
            error = $"was {parsed.ToString(CultureInfo.InvariantCulture)} on {raw.Field}, "
                + $"outside {minimum.ToString(CultureInfo.InvariantCulture)}"
                + $"-{maximum.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        number = parsed;
        return true;
    }

    // The portal states the unit in the field name, which is the only reason this can be converted
    // rather than guessed at from magnitude.
    private static double? NormaliseRange(List<VwGroupSnapshot> snapshots, double? range)
    {
        if (range is not { } value || Latest(snapshots, VwGroupFieldNames.RangeKm) is not { } raw)
        {
            return range;
        }

        return VwGroupFieldNames.Matches(raw.Field, VwGroupFieldNames.RangeInMetres) ? value / 1000 : value;
    }

    private static double? Largest(List<VwGroupSnapshot> snapshots, string[] candidates)
    {
        double? largest = null;

        foreach (var snapshot in snapshots)
        {
            foreach (var (field, value) in snapshot.Values)
            {
                if (!VwGroupFieldNames.Matches(field, candidates) || VwGroupFieldNames.IsSentinel(value))
                {
                    continue;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && !double.IsNaN(parsed)
                    && (largest is null || parsed > largest))
                {
                    largest = parsed;
                }
            }
        }

        return largest;
    }

    private static TEnum MapEnum<TEnum>(
        List<VwGroupSnapshot> snapshots, string[] candidates,
        (string Value, TEnum State)[] vocabulary, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (Latest(snapshots, candidates) is not { } raw)
        {
            return fallback;
        }

        foreach (var (word, state) in vocabulary)
        {
            if (string.Equals(raw.Value, word, StringComparison.OrdinalIgnoreCase))
            {
                return state;
            }
        }

        // The one place an unrecognised value does not reject the bundle. See the type's summary.
        return fallback;
    }

    /// <summary>The newest snapshot in which one of these names carried something that is not a sentinel.</summary>
    private static (string Field, string Value)? Latest(List<VwGroupSnapshot> snapshots, string[] candidates)
    {
        for (var index = snapshots.Count - 1; index >= 0; index--)
        {
            foreach (var (field, value) in snapshots[index].Values)
            {
                if (VwGroupFieldNames.Matches(field, candidates) && !VwGroupFieldNames.IsSentinel(value))
                {
                    return (field, value);
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Unmapped(List<VwGroupSnapshot> snapshots)
    {
        string[][] known =
        [
            VwGroupFieldNames.StateOfCharge, VwGroupFieldNames.RangeKm,
            VwGroupFieldNames.ChargeTimeRemainingMinutes, VwGroupFieldNames.ChargeState,
            VwGroupFieldNames.PlugState, .. VwGroupFieldNames.KnownButUnused,
        ];

        var unmapped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in snapshots)
        {
            foreach (var field in snapshot.Values.Keys)
            {
                if (!known.Any(candidates => VwGroupFieldNames.Matches(field, candidates))
                    && !VwGroupReportBundle.TimestampProperties.Contains(
                        VwGroupFieldNames.Leaf(field), StringComparer.OrdinalIgnoreCase))
                {
                    unmapped.Add(field);
                }
            }
        }

        return [.. unmapped];
    }
}
