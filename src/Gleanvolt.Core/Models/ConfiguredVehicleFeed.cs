using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Core.Models;

/// <summary>
/// The one feed this installation reads its car from, or none (issue #212).
///
/// <para><b>One car, one feed.</b> A car plugin (<c>Vehicle:External</c>), the owner's own MQTT topic, or nothing.
/// The composition root settles which, so every surface that quotes the feed's health (the dashboard, the feed band,
/// Health, Home Assistant) is quoting the feed that produced the reading beside it.</para>
/// </summary>
/// <param name="Service">The feed, or null when no feed is configured — a supported installation.</param>
public sealed record ConfiguredVehicleFeed(IVehicleUpdateService? Service)
{
    /// <summary>No feed at all.</summary>
    public static ConfiguredVehicleFeed None { get; } = new(Service: null);

    /// <summary>The feed's health, or null when there is no feed.</summary>
    public VehicleSourceHealth? Health => Service?.Health;
}
