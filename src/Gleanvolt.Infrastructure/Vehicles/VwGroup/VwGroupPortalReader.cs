using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// The VW Group portal behind <see cref="IVehiclePortalReader"/>, so the web UI can ask for the car
/// with a button instead of an owner running a console harness with four environment variables.
///
/// <para><b>A fresh session per press.</b> Each read builds its own cookie jar and signs in again,
/// rather than holding a session between presses. That is the right trade for a button pressed by
/// hand a few times a day: a held session that has quietly expired fails in a way that looks like bad
/// credentials, and a button pressed twice costs two sign-ins rather than ninety-six a day.
/// <see cref="VwGroupUpdateService"/> is where holding one starts to pay, and where a session's real
/// lifetime is measured.</para>
///
/// <para><b>Nothing here throws for an expected failure.</b> A refused password, a consent screen, a
/// portal with nothing to give — each becomes an unsuccessful reading carrying its kind, because the
/// kinds are what the page turns into different advice. Only a genuine bug escapes.</para>
/// </summary>
public sealed class VwGroupPortalReader(
    VwGroupPortalOptions options, ILogger<VwGroupPortalReader>? logger = null) : IVehiclePortalReader
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public string PortalName => "VW Group EU Data Act portal";

    public bool IsConfigured => options.IsConfigured;

    public string DescribeWhatIsMissing() => options.DescribeWhatIsMissing();

    public async Task<VehiclePortalReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return VehiclePortalReading.Failed(
                nameof(VwGroupFailure.NotConfigured),
                $"The portal client needs {DescribeWhatIsMissing()}.",
                worthRetrying: false);
        }

        using var handler = VwGroupSignIn.CreateHandler(new CookieContainer());
        using var http = new HttpClient(handler);

        var client = new VwGroupPortalClient(http, options, logger: _logger);

        try
        {
            var vehicle = await client.GetVehicleAsync(cancellationToken).ConfigureAwait(false);
            var (requestId, name) = await client.GetNewestDatasetAsync(vehicle, cancellationToken)
                .ConfigureAwait(false);
            var archive = await client.DownloadAsync(vehicle.Vin, requestId, name, cancellationToken)
                .ConfigureAwait(false);

            if (!VwGroupReportBundle.TryRead(archive, out var snapshots, out var bundleError, out var bundle))
            {
                return VehiclePortalReading.Failed(
                    nameof(VwGroupFailure.UnusableData), bundleError!, worthRetrying: false,
                    diagnostics: Notes(bundle), dropped: bundle.UndatedFields);
            }

            var mapped = VwGroupVehicleStateMapper.Map(snapshots, vehicle.MaskedVin);

            if (mapped.State is null)
            {
                // #73's rule, surfaced rather than swallowed: present-but-unusable is rejected whole,
                // and the reason is the diagnosis. The unrecognised names travel with it, because the
                // commonest reason to be here is a vocabulary that matched nothing -- and then the
                // list *is* the fix.
                _logger.LogWarning(
                    "The VW portal read of {Vehicle} produced no usable reading: {Reason}",
                    vehicle.MaskedVin, mapped.Error);

                return VehiclePortalReading.Failed(
                    nameof(VwGroupFailure.UnusableData), mapped.Error!, worthRetrying: false,
                    unmapped: mapped.UnmappedFields,
                    matched: mapped.MatchedFields,
                    diagnostics: Notes(bundle), dropped: bundle.UndatedFields);
            }

            _logger.LogInformation(
                "Read {Vehicle} from the VW portal: {Snapshots} snapshot(s), {Unmapped} unrecognised field(s).",
                vehicle.MaskedVin, snapshots.Count, mapped.UnmappedFields.Count);

            return new VehiclePortalReading(
                Succeeded: true,
                State: mapped.State,
                Vehicle: vehicle.MaskedVin,
                SnapshotCount: snapshots.Count,
                OldestSnapshot: snapshots[0].CapturedAt,
                NewestSnapshot: snapshots[^1].CapturedAt,
                TargetSocPercent: mapped.TargetSocPercent,
                OdometerKm: mapped.OdometerKm,
                UnmappedFields: mapped.UnmappedFields,
                MatchedFields: mapped.MatchedFields,
                Diagnostics: Notes(bundle),
                DroppedFields: bundle.UndatedFields);
        }
        catch (VwGroupPortalException failure)
        {
            // Expected, and the kind is the point -- one of these wants a browser and must never be
            // retried, another is ordinary and is fixed by pressing the button again.
            _logger.LogWarning(
                "The VW portal read did not produce a reading ({Failure}): {Reason}",
                failure.Failure, failure.Message);

            return VehiclePortalReading.Failed(
                failure.Failure.ToString(), failure.Message, failure.IsWorthRetrying);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>
    /// What the bundle held that never became a reading, in sentences a page can show. Empty when
    /// every report in it became a snapshot, which is the ordinary case and deserves no words.
    /// </summary>
    private static IReadOnlyList<string> Notes(VwGroupBundleReport bundle)
    {
        var notes = new List<string>();

        if (bundle.Undated > 0)
        {
            // The one that hides a battery report: dropped whole, and its field names dropped with it,
            // so the unrecognised list cannot show what was in there.
            notes.Add(
                $"{bundle.Undated} of {bundle.Reports} report(s) were dropped for carrying no "
                + "timestamp this build recognises. Their fields are listed below and are invisible "
                + "everywhere else — a timestamp spelled a new way costs the whole report.");
        }

        if (bundle.Empty > 0)
        {
            notes.Add($"{bundle.Empty} report(s) carried no named values at all.");
        }

        return notes;
    }
}
