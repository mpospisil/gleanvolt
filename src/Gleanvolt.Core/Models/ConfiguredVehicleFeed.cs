using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Core.Models;

/// <summary>
/// The one feed this installation reads its car from, or none (issue #212).
///
/// <para><b>One car, one feed.</b> A Volkswagen with <c>Vehicle:Website</c> reads volkswagen.de, a
/// Škoda with <c>Vehicle:Skoda</c> reads MyŠkoda, and only a car with neither — a Cupra, another
/// Group brand — reads the Data Act portal. The composition root settles which, so every surface that
/// quotes the feed's health (the dashboard, the sign-in banner, Health, Home Assistant) is quoting the
/// feed that produced the reading beside it, rather than whichever service happened to be registered
/// first.</para>
/// </summary>
/// <param name="Service">The feed, or null when no feed is configured — a supported installation.</param>
/// <param name="SetAside">
/// A sentence for the startup log when a configured feed was not started because another one
/// reads this car better — the Data Act portal beside <c>Vehicle:Website</c>. Null otherwise.
/// </param>
public sealed record ConfiguredVehicleFeed(IVehicleUpdateService? Service, string? SetAside = null)
{
    /// <summary>No feed at all.</summary>
    public static ConfiguredVehicleFeed None { get; } = new(Service: null);

    /// <summary>The feed's health, or null when there is no feed.</summary>
    public VehicleSourceHealth? Health => Service?.Health;
}
