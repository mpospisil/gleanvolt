using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Whether Gleanvolt can control the charger (#441), in one place the control loop, the web UI, the API and Home
/// Assistant read. Updated on every poll.
/// </summary>
public interface IChargerAvailability
{
    /// <summary>As of the last poll, the grace period applied.</summary>
    ChargerAvailability Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes state or reason.</summary>
    event Action? Changed;
}
