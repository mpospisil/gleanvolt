using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Whether this run is in setup mode, and what a setup still needs (#301).
///
/// <para>Two answers, because they can differ: <see cref="SetupMode"/> is how this process started and
/// does not change until it restarts -- the controller either was registered or was not.
/// <see cref="Current"/> is judged afresh from what has been saved since, so a page can say "that was
/// the last required setting; restart to start the controller".</para>
/// </summary>
public interface ISetupState
{
    /// <summary>True when the controller was not started because the configuration was not usable.</summary>
    bool SetupMode { get; }

    /// <summary>The configuration this process started with, as judged at startup.</summary>
    SetupStatus AtStartup { get; }

    /// <summary>The configuration as saved now, overrides file and secret store included.</summary>
    SetupStatus Current();
}

/// <summary>
/// The answer for a host that has no setup mode: a test host, or a UI hosted over a controller that is
/// already running.
/// </summary>
public sealed class NoSetupState : ISetupState
{
    private static readonly SetupStatus Complete = new([], []);

    public bool SetupMode => false;

    public SetupStatus AtStartup => Complete;

    public SetupStatus Current() => Complete;
}
