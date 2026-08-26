namespace Gleanvolt.Core.Models;

/// <summary>
/// The car as everything downstream sees it (issue #124): one resolved, validated snapshot, built once
/// at startup from <see cref="EvOptions"/>.
///
/// <para>The <see cref="PvSystemInfo"/> arrangement, and for the same reason: no consumer should have
/// to assemble the car from configuration keys of its own, and none should have to know whether a
/// figure was stated or defaulted. What is stated is resolved once, here, and everything else asks.</para>
///
/// <para><b>An unconfigured car is a valid state.</b> <see cref="Unknown"/> is what an installation
/// that has never filled the section in runs on, and it narrows nothing — every limit falls back to the
/// installation's, exactly as before this existed.</para>
/// </summary>
/// <param name="Id">The car's stable identity, or empty while nothing has claimed one.</param>
/// <param name="Name">The display name; falls back to <paramref name="Id"/>, then to the model.</param>
/// <param name="Make">The manufacturer. Reported, never acted on.</param>
/// <param name="Model">The model. Reported, never acted on.</param>
/// <param name="BatteryCapacityKWh">Usable capacity, or 0 when it has not been configured.</param>
/// <param name="ChargeEfficiency">Charger-meter → cells efficiency, (0..1].</param>
/// <param name="Phases">
/// Phases the <b>car</b> can charge on, or null when unstated. Null narrows nothing; a stated value
/// caps the installation's — see <see cref="ChargingLimits"/>.
/// </param>
/// <param name="MinChargingCurrentAmps">The lowest current the car will start on, or null when unstated.</param>
/// <param name="MaxChargingCurrentAmps">The car's own ceiling, or null when unstated.</param>
/// <param name="TelemetryTopic">Where this car's readings arrive, or empty when no feed names one.</param>
public sealed record EvInfo(
    string Id,
    string Name,
    string Make,
    string Model,
    double BatteryCapacityKWh,
    double ChargeEfficiency,
    int? Phases,
    int? MinChargingCurrentAmps,
    int? MaxChargingCurrentAmps,
    string TelemetryTopic)
{
    /// <summary>
    /// The car nobody has described. Every figure unstated, so every limit is the installation's and
    /// every behaviour is what it was before this type existed.
    /// </summary>
    public static EvInfo Unknown { get; } = new(
        Id: string.Empty,
        Name: string.Empty,
        Make: string.Empty,
        Model: string.Empty,
        BatteryCapacityKWh: 0,
        ChargeEfficiency: 0.9,
        Phases: null,
        MinChargingCurrentAmps: null,
        MaxChargingCurrentAmps: null,
        TelemetryTopic: string.Empty);

    /// <summary>
    /// Whether anything at all has been said about the car.
    ///
    /// <para>Judged on the values rather than on which instance this is, because the two ways of
    /// saying nothing must read the same: an absent <c>Ev</c> section and a section holding one entry
    /// with every field left at its default are the same fact, and the shipped <c>appsettings.json</c>
    /// is the second of them.</para>
    ///
    /// <para><see cref="TelemetryTopic"/> is deliberately not part of the test. A topic is the link
    /// between a car and the feed that reports on it, not a fact about the car — and it has a default,
    /// so counting it would make every installation look like it had described a vehicle.</para>
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Id)
        || !string.IsNullOrWhiteSpace(Name)
        || !string.IsNullOrWhiteSpace(Make)
        || !string.IsNullOrWhiteSpace(Model)
        || BatteryCapacityKWh > 0
        || Phases is not null
        || MinChargingCurrentAmps is not null
        || MaxChargingCurrentAmps is not null;

    /// <summary>Whether a target may be asked for as a state of charge — i.e. whether a capacity is known.</summary>
    public bool CanTargetSoc => BatteryCapacityKWh > 0;

    /// <summary>The pack's usable capacity in watt-hours, which is what the conversions are stated in.</summary>
    public double BatteryCapacityWh => BatteryCapacityKWh * 1000;

    /// <summary>The pack figures in the shape the charge factories take.</summary>
    public VehiclePackLimits Pack => new(BatteryCapacityKWh, ChargeEfficiency);

    /// <summary>
    /// One line for the log at startup, so a <c>docker logs</c> dump answers "and what car did it think
    /// it had?" without the configuration beside it — the same service <see cref="PvSystemInfo.Describe"/>
    /// does for the installation.
    /// </summary>
    public string Describe()
    {
        if (!IsConfigured)
        {
            return "no vehicle configured; every charging limit is the installation's";
        }

        var what = string.Join(" ", new[] { Make, Model }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var name = string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(Id) ? "unnamed" : Id) : Name;

        var pack = CanTargetSoc ? $"{BatteryCapacityKWh:0.#}kWh usable" : "capacity not configured";

        var accepts = (MinChargingCurrentAmps, MaxChargingCurrentAmps, Phases) switch
        {
            (null, null, null) => "no charging limits stated",
            _ => "accepts "
                + $"{(MinChargingCurrentAmps is { } min ? $"{min}" : "?")}-"
                + $"{(MaxChargingCurrentAmps is { } max ? $"{max}" : "?")}A on "
                + $"{(Phases is { } phases ? $"{phases}" : "?")} phase(s)",
        };

        return string.IsNullOrWhiteSpace(what)
            ? $"{name} ({pack}, {accepts})"
            : $"{name} — {what} ({pack}, {accepts})";
    }
}
