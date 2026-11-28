using Gleanvolt.Providers.Contracts.Vehicle;

namespace Gleanvolt.Providers.Testing;

/// <summary>How <see cref="VehicleTestHub"/> answers, as a Gleanvolt configured this way would.</summary>
public sealed class VehicleTestHubOptions
{
    /// <summary>The car Gleanvolt is configured for, sent in the welcome.</summary>
    public VehicleInfo Car { get; set; } = new("test-car", "Test car", "Volkswagen", "ID.4", 77, "Europe/Prague");

    /// <summary>Whether Gleanvolt has a charging session open as the plugin says hello. Null as from a Gleanvolt older than vehicle 1.2.</summary>
    public ChargingSessionInfo? Session { get; set; } = ChargingSessionInfo.Closed;

    /// <summary>The token the plugin must send. A random one when null.</summary>
    public string? Token { get; set; }

    /// <summary>Whether a plugin without a token is paired at once (true), or the pairing endpoints answer 404 as an older Gleanvolt.</summary>
    public bool AcceptPairing { get; set; } = true;

    /// <summary>How long each wait lasts before it fails.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gleanvolt's clock, for the readings' capture times.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;
}
