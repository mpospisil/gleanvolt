using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Providers.Testing;

/// <summary>How a <see cref="ChargerTestHub"/> welcomes the plugin and how long it waits for it.</summary>
public sealed class ChargerTestHubOptions
{
    /// <summary>The charge control's limits, as the welcome tells the plugin.</summary>
    public ChargerLimits Limits { get; set; } = new(6, 16, 3, 230);

    /// <summary>Whether the welcome says Gleanvolt is in dry run.</summary>
    public bool DryRun { get; set; }

    /// <summary>The installation's IANA time zone, as the welcome tells the plugin.</summary>
    public string TimeZone { get; set; } = "Europe/Prague";

    /// <summary>The token the plugin must connect with. A new random one when null.</summary>
    public string? Token { get; set; }

    /// <summary>How long a read, a command or a wait for the plugin may take: 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The clock a reading's time is checked against.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;
}
