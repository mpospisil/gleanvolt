namespace Gleanvolt.Hosting.Chargers;

/// <summary>
/// How long Gleanvolt waits for a charger behind a plugin (#435), bound from the <c>"ExternalCharger"</c>
/// section. The defaults suit a plugin on the same machine or the same LAN talking to a charger on the LAN;
/// none of them needs to be set.
/// </summary>
public sealed class ExternalChargerOptions
{
    public const string SectionName = "ExternalCharger";

    /// <summary>
    /// How long a read waits for the plugin's answer. A read that runs out is a charger Gleanvolt could not
    /// read: the poll reports it as unknown, and the control loop makes no decision on it.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a command waits for the plugin to say it is done. One that runs out failed.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A reading received this recently is used as it is rather than asked for again: the control loop reads
    /// the status and then the settings in the same tick, and one answer serves both. A command since makes
    /// the held reading stale at once.
    /// </summary>
    public TimeSpan ReadingMaxAge { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The charger is behind a plugin, whatever <c>Pv:Chargers:0:Driver</c> says (#444). Set by the deploy scripts when a
    /// charger plugin is in GLEANVOLT_PLUGINS, so switching to the plugin needs no other change and the built-in driver
    /// never opens the charger beside it. The address in <c>Pv:Chargers</c> is then the plugin's alone.
    /// </summary>
    public bool UsePlugin { get; init; }
}
