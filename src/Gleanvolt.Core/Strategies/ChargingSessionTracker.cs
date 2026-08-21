using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Turns the stream of <see cref="ChargeControlStatus"/> snapshots the poll loop publishes into
/// sessions, samples and events. Pure and framework-free in the spirit of the other strategies here:
/// no clock of its own beyond the injected <see cref="TimeProvider"/> (used only for the zone id), no
/// I/O, no logging. Persisting what it returns is somebody else's job.
///
/// <para><b>Boundaries.</b> A session opens when a controlling mode is driving a connected car, and
/// closes when that stops being true. A mode switched mid-session does <em>not</em> split the session
/// — it records a <see cref="ChargingSessionEventKind.ModeChanged"/> event — because "Forecasted all
/// afternoon, then FastNoBattery at 17:00" is one story about one car, and splitting it would lose
/// exactly the comparison worth having.</para>
///
/// <para><b>Cadence.</b> Every observation is integrated into the running energy totals, so those are
/// as accurate as the poll interval allows. Only some observations become stored samples: one every
/// <c>sampleInterval</c>, plus a forced one whenever anything changes. That keeps a four-hour session
/// at a few hundred rows without blurring a single transition.</para>
/// </summary>
public sealed class ChargingSessionTracker
{
    private readonly TimeSpan _sampleInterval;
    private readonly bool _recordUncontrolled;
    private readonly TimeProvider _timeProvider;

    // The running totals. They integrate on every observation, not on every stored sample.
    //
    // The first five are about the car: what it received and where each watt came from. The last three
    // are about the *site* over the same window — what the roof made, what the forecast said it would
    // make, and what came off the grid — which is a different question and not derivable from the
    // first five, since the house and the home battery take a share of both.
    private readonly EnergyIntegrator _delivered = new();
    private readonly EnergyIntegrator _fromSolar = new();
    private readonly EnergyIntegrator _fromGrid = new();
    private readonly EnergyIntegrator _fromBattery = new();
    private readonly EnergyIntegrator _loaned = new();
    private readonly EnergyIntegrator _solar = new();
    private readonly EnergyIntegrator _forecastSolar = new();
    private readonly EnergyIntegrator _gridImport = new();

    // Whether the forecast integrator has ever been given a reading. Without it a session recorded
    // with no forecast at all would report 0 Wh expected, which reads as "the forecast predicted
    // nothing" rather than "there was no forecast".
    private bool _forecastSeen;

    // The last vehicle reading handed in, carried so a sample forced between telemetry messages still
    // reports the car's SOC rather than a hole. The feed updates in minutes at best.
    private VehicleState? _vehicle;

    private ChargingSession? _open;
    private DateTimeOffset? _lastSampleAt;
    private double _peakPowerWatts;

    // The previous observation's shape, so a change can be noticed once and turned into an event
    // rather than re-detected on every poll.
    private ChargeControlMode _lastMode;
    private ChargeControlState _lastState;
    private int? _lastTargetCurrentAmps;
    private bool _lastHoldActive;
    private bool? _lastPlanUsable;
    private double _lastSocPercent;

    /// <param name="sampleInterval">How often to store a sample; changes force one regardless.</param>
    /// <param name="recordUncontrolled">
    /// Also record a plugged-in car that no mode is driving, as a baseline to compare controlled
    /// sessions against. Off by default: it produces sessions we did nothing to.
    /// </param>
    public ChargingSessionTracker(
        TimeSpan sampleInterval,
        bool recordUncontrolled = false,
        TimeProvider? timeProvider = null)
    {
        _sampleInterval = sampleInterval > TimeSpan.Zero ? sampleInterval : TimeSpan.FromSeconds(30);
        _recordUncontrolled = recordUncontrolled;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The session currently being recorded, or null when none is open.</summary>
    public ChargingSession? OpenSession => _open;

    /// <summary>
    /// Observes one poll. Returns what the store should do about it — which may well be nothing.
    /// </summary>
    /// <param name="forecastPowerWatts">
    /// What the solar forecast expected at this instant, if a forecast has been fetched. Passed in
    /// rather than looked up so this type stays free of the forecast service.
    /// </param>
    /// <param name="forecastRemainingTodayWh">Forecast PV still to come today; recorded on the session header at open.</param>
    /// <param name="vehicle">
    /// The car's own last-known state, for the same reason and on the same terms as the forecast: passed
    /// in rather than looked up, and purely advisory. Its reading may be hours old, so its capture time
    /// is recorded next to it and nothing here treats it as live.
    /// </param>
    public ChargingSessionUpdate Observe(
        ChargeControlStatus status,
        double? forecastPowerWatts = null,
        double? forecastRemainingTodayWh = null,
        VehicleState? vehicle = null)
    {
        var shouldRecord = ShouldRecord(status);
        if (_open is null && !shouldRecord)
        {
            return ChargingSessionUpdate.None;
        }

        var now = status.Timestamp;
        var events = new List<ChargingSessionEvent>();
        ChargingSession? started = null;

        if (_open is null)
        {
            started = OpenSessionAt(status, forecastRemainingTodayWh);
            events.Add(Event(now, ChargingSessionEventKind.SessionStarted, $"{status.Mode} took control of a connected car."));
        }
        else
        {
            events.AddRange(DetectChanges(status));
        }

        Integrate(status, forecastPowerWatts, vehicle);

        // A session that began while nothing was controlling becomes a controlled one the moment a
        // mode takes over, rather than staying labelled as a bystander for its whole length.
        if (!_open!.Controlled && IsControlling(status.Mode))
        {
            _open = _open with { Controlled = true };
        }

        var ending = !shouldRecord;
        var due = _lastSampleAt is not { } last || now - last >= _sampleInterval;
        ChargingSessionSample? sample = null;

        if (started is not null || events.Count > 0 || ending || due)
        {
            sample = BuildSample(status, forecastPowerWatts);
            _lastSampleAt = now;
        }

        ChargingSession? ended = null;
        if (ending)
        {
            ended = CloseAt(EndReasonFor(status), now, status.Mode);
            events.Add(Event(now, ChargingSessionEventKind.SessionEnded, DescribeEnd(ended.EndReason!.Value, ended)));
            Reset();
        }

        return new ChargingSessionUpdate(started, sample, events, ended);
    }

    /// <summary>
    /// Ends an open session without a further observation — the graceful-shutdown path. Returns
    /// <see cref="ChargingSessionUpdate.None"/> when nothing was open.
    /// </summary>
    public ChargingSessionUpdate Close(ChargingSessionEndReason reason, DateTimeOffset at)
    {
        if (_open is null)
        {
            return ChargingSessionUpdate.None;
        }

        var ended = CloseAt(reason, at, _lastMode);
        var events = new List<ChargingSessionEvent>
        {
            Event(at, ChargingSessionEventKind.SessionEnded, DescribeEnd(reason, ended)),
        };

        Reset();
        return new ChargingSessionUpdate(null, null, events, ended);
    }

    private bool ShouldRecord(ChargeControlStatus status)
    {
        // Unknown is a charger that has stopped answering, and it says nothing about the plug. An open
        // session rides one out rather than being closed and filed as "the car was unplugged"; a
        // session that has not started yet still needs a positive reading before it may begin.
        var connected = status.CarConnected
            || (_open is not null && !status.ChargerStatus.IsConnectionKnown());

        if (!connected)
        {
            return false;
        }

        return IsControlling(status.Mode) || _recordUncontrolled;
    }

    // Off is the only mode that isn't controlling: it leaves the charger exactly as its owner set it.
    private static bool IsControlling(ChargeControlMode mode) =>
        mode is ChargeControlMode.Solar or ChargeControlMode.Forecasted or ChargeControlMode.FastNoBattery
            or ChargeControlMode.Targeted;

    private ChargingSession OpenSessionAt(ChargeControlStatus status, double? forecastRemainingTodayWh)
    {
        _open = new ChargingSession(
            // UUIDv7: time-ordered, so ids sort chronologically as object keys and index well.
            Id: Guid.CreateVersion7(status.Timestamp),
            StartedAt: status.Timestamp,
            EndedAt: null,
            TimeZoneId: _timeProvider.LocalTimeZone.Id,
            StartMode: status.Mode,
            EndMode: status.Mode,
            EndReason: null,
            StartBatterySocPercent: status.BatterySocPercent,
            EndBatterySocPercent: null,
            EnergyDeliveredWh: 0,
            FromSolarWh: 0,
            FromGridWh: 0,
            FromBatteryWh: 0,
            LoanedWh: 0,
            PeakChargingPowerWatts: 0,
            StartPlan: status.Plan,
            ForecastRemainingAtStartWh: forecastRemainingTodayWh,
            Controlled: IsControlling(status.Mode));

        _lastMode = status.Mode;
        _lastState = status.State;
        _lastTargetCurrentAmps = status.TargetCurrentAmps;
        _lastHoldActive = status.BatteryHoldActive;
        _lastPlanUsable = status.Plan?.IsUsable;
        _lastSampleAt = null;
        _peakPowerWatts = 0;

        return _open;
    }

    // Deliberately eager rather than an iterator: it mutates the "last seen" fields as it goes, and a
    // lazy sequence whose side effects depend on somebody enumerating it is a trap waiting to be set.
    private List<ChargingSessionEvent> DetectChanges(ChargeControlStatus status)
    {
        var now = status.Timestamp;
        var events = new List<ChargingSessionEvent>();

        if (status.Mode != _lastMode)
        {
            events.Add(Event(now, ChargingSessionEventKind.ModeChanged, $"{_lastMode} → {status.Mode}."));
            _lastMode = status.Mode;
        }

        if (status.State != _lastState)
        {
            if (status.State == ChargeControlState.Charging)
            {
                events.Add(Event(now, ChargingSessionEventKind.ChargingStarted, $"Charging at {Describe(status.TargetCurrentAmps)}."));
            }
            else if (status.State == ChargeControlState.Paused)
            {
                events.Add(Event(now, ChargingSessionEventKind.ChargingPaused, "Charging paused."));
            }

            _lastState = status.State;
        }

        if (status.TargetCurrentAmps != _lastTargetCurrentAmps)
        {
            events.Add(Event(
                now,
                ChargingSessionEventKind.TargetCurrentChanged,
                $"{Describe(_lastTargetCurrentAmps)} → {Describe(status.TargetCurrentAmps)}."));
            _lastTargetCurrentAmps = status.TargetCurrentAmps;
        }

        if (status.BatteryHoldActive != _lastHoldActive)
        {
            events.Add(status.BatteryHoldActive
                ? Event(now, ChargingSessionEventKind.BatteryHoldArmed, $"Hold armed at {status.BatteryHoldTargetWatts:F0}W.")
                : Event(now, ChargingSessionEventKind.BatteryHoldReleased, "Hold released."));
            _lastHoldActive = status.BatteryHoldActive;
        }

        // Only meaningful while a plan is being published at all, i.e. in the Forecasted mode.
        var planUsable = status.Plan?.IsUsable;
        if (planUsable is not null)
        {
            if (planUsable != _lastPlanUsable)
            {
                events.Add(planUsable.Value
                    ? Event(now, ChargingSessionEventKind.PlanUsable, status.Plan!.Reason)
                    : Event(now, ChargingSessionEventKind.PlanUnusable, status.Plan!.Reason));
            }

            _lastPlanUsable = planUsable;
        }

        return events;
    }

    private void Integrate(ChargeControlStatus status, double? forecastPowerWatts, VehicleState? vehicle)
    {
        var now = status.Timestamp;
        var evPower = Math.Max(0, status.EvChargerPowerWatts);
        var split = ChargingSourceAttribution.Split(status);

        _delivered.Add(now, evPower);
        _fromSolar.Add(now, split.SolarWatts);
        _fromGrid.Add(now, split.GridWatts);
        _fromBattery.Add(now, split.BatteryWatts);
        _loaned.Add(now, status.LoanPowerWatts);

        _solar.Add(now, Math.Max(0, status.SolarPowerWatts));

        // Positive grid power is import in this model; export is a different quantity and is not
        // netted off, or a sunny hour would cancel out the evening the car actually cost money.
        _gridImport.Add(now, Math.Max(0, status.GridPowerWatts));

        if (forecastPowerWatts is { } forecastWatts)
        {
            _forecastSolar.Add(now, Math.Max(0, forecastWatts));
            _forecastSeen = true;
        }

        // Only overwritten by an actual reading: a feed that goes quiet mid-session should leave the
        // last SOC standing with its own (now visibly ageing) capture time, not blank the column.
        if (vehicle is not null)
        {
            _vehicle = vehicle;
        }

        _peakPowerWatts = Math.Max(_peakPowerWatts, evPower);
        _lastSocPercent = status.BatterySocPercent;
    }

    private ChargingSessionSample BuildSample(ChargeControlStatus status, double? forecastPowerWatts)
    {
        var split = ChargingSourceAttribution.Split(status);

        return new ChargingSessionSample(
            SessionId: _open!.Id,
            Timestamp: status.Timestamp,
            Mode: status.Mode,
            State: status.State,
            ChargerStatus: status.ChargerStatus,
            BatterySocPercent: status.BatterySocPercent,
            SolarPowerWatts: status.SolarPowerWatts,
            GridPowerWatts: status.GridPowerWatts,
            BatteryPowerWatts: status.BatteryPowerWatts,
            EvChargerPowerWatts: status.EvChargerPowerWatts,
            EvChargingCurrentAmps: status.EvChargingCurrentAmps,
            ActiveCurrentAmps: status.ActiveCurrentAmps,
            TargetCurrentAmps: status.TargetCurrentAmps,
            FromSolarWatts: split.SolarWatts,
            FromGridWatts: split.GridWatts,
            FromBatteryWatts: split.BatteryWatts,
            EnergyDeliveredWh: _delivered.EnergyWattHours,
            FromSolarWh: _fromSolar.EnergyWattHours,
            FromGridWh: _fromGrid.EnergyWattHours,
            FromBatteryWh: _fromBattery.EnergyWattHours,
            LoanedWh: _loaned.EnergyWattHours,
            SolarWh: _solar.EnergyWattHours,
            ForecastSolarWh: _forecastSeen ? _forecastSolar.EnergyWattHours : null,
            GridImportWh: _gridImport.EnergyWattHours,
            VehicleSocPercent: _vehicle?.SocPercent,
            // Only meaningful next to a SOC: a capture time on its own would say the feed is alive
            // while the column it explains is empty.
            VehicleSocCapturedAt: _vehicle?.SocPercent is null ? null : _vehicle.CapturedAt,
            // Stored as a plain number with a companion flag rather than as a nullable: a chart wants
            // a series it can draw, and "the car didn't say" is carried by the flag next to it. Zero
            // on its own is ambiguous -- it is also what a car that has finished reports.
            VehicleChargeTimeRemainingMinutes: _vehicle?.ChargeTimeRemaining?.TotalMinutes ?? 0,
            VehicleChargeTimeRemainingReported: _vehicle?.ChargeTimeRemaining is not null,
            SurplusWatts: status.SurplusWatts,
            LoanPowerWatts: status.LoanPowerWatts,
            BatteryHoldActive: status.BatteryHoldActive,
            ForecastPowerWatts: forecastPowerWatts,
            PlanRemainingPvWh: status.Plan?.RemainingPvWh,
            PlanFeasibleEvEnergyWh: status.Plan?.FeasibleEvEnergyWh,
            PlanRequiredSocFloorPercent: status.Plan?.RequiredSocFloorPercent);
    }

    private ChargingSession CloseAt(ChargingSessionEndReason reason, DateTimeOffset at, ChargeControlMode endMode) =>
        _open! with
        {
            EndedAt = at,
            EndMode = endMode,
            EndReason = reason,
            EndBatterySocPercent = _lastSocPercent,
            EnergyDeliveredWh = _delivered.EnergyWattHours,
            FromSolarWh = _fromSolar.EnergyWattHours,
            FromGridWh = _fromGrid.EnergyWattHours,
            FromBatteryWh = _fromBattery.EnergyWattHours,
            LoanedWh = _loaned.EnergyWattHours,
            PeakChargingPowerWatts = _peakPowerWatts,
        };

    private static ChargingSessionEndReason EndReasonFor(ChargeControlStatus status) => status switch
    {
        // Checked before the others: by the time this status is published the loop has already
        // returned the mode to Off, so every other signal says "switched off" instead of "car full".
        { SessionCompleted: true } => ChargingSessionEndReason.SessionComplete,
        { CarConnected: false } => ChargingSessionEndReason.CarUnplugged,
        _ => ChargingSessionEndReason.ModeOff,
    };

    private static string DescribeEnd(ChargingSessionEndReason reason, ChargingSession session)
    {
        var delivered = $"{session.EnergyDeliveredWh / 1000:F2}kWh delivered";
        return reason switch
        {
            ChargingSessionEndReason.SessionComplete => $"The car finished charging; {delivered}.",
            ChargingSessionEndReason.CarUnplugged => $"The car was unplugged; {delivered}.",
            ChargingSessionEndReason.ModeOff => $"Charge control returned to Off; {delivered}.",
            ChargingSessionEndReason.ServiceStopped => $"The service stopped; {delivered}.",
            _ => $"The session was interrupted; {delivered}.",
        };
    }

    private static string Describe(int? amps) => amps is { } value ? $"{value}A" : "off";

    private ChargingSessionEvent Event(DateTimeOffset at, ChargingSessionEventKind kind, string detail) =>
        new(_open!.Id, at, kind, detail);

    private void Reset()
    {
        _open = null;
        _lastSampleAt = null;
        _peakPowerWatts = 0;
        _lastTargetCurrentAmps = null;
        _lastPlanUsable = null;
        _lastHoldActive = false;

        _delivered.Reset();
        _fromSolar.Reset();
        _fromGrid.Reset();
        _fromBattery.Reset();
        _loaned.Reset();
        _solar.Reset();
        _forecastSolar.Reset();
        _gridImport.Reset();

        _forecastSeen = false;
        _vehicle = null;
    }
}

/// <summary>
/// What one observation changed. Every member is optional: most polls change nothing and return
/// <see cref="None"/>.
/// </summary>
/// <param name="Started">A session that has just opened, whose header should be written.</param>
/// <param name="Sample">A sample to append, when this observation was due one or forced one.</param>
/// <param name="Events">Events to append; usually empty.</param>
/// <param name="Ended">A session that has just closed, whose header should be finalised.</param>
public sealed record ChargingSessionUpdate(
    ChargingSession? Started,
    ChargingSessionSample? Sample,
    IReadOnlyList<ChargingSessionEvent> Events,
    ChargingSession? Ended)
{
    /// <summary>Nothing happened.</summary>
    public static readonly ChargingSessionUpdate None = new(null, null, [], null);

    /// <summary>Whether there is nothing at all for the store to do.</summary>
    public bool IsEmpty => Started is null && Sample is null && Events.Count == 0 && Ended is null;
}
