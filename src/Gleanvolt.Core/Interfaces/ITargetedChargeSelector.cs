using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The active <see cref="TargetedChargeRequest"/> — how much energy by when — held at runtime and
/// changeable from any surface, exactly as <see cref="IChargeControlModeSelector"/> holds the mode.
/// The polling loop reads <see cref="Request"/> each cycle.
///
/// <para>Two seams rather than one, deliberately: the request and the mode are set separately, so a
/// request can be revised while the mode is running, and selecting
/// <see cref="Enums.ChargeControlMode.Targeted"/> with nothing requested is a state the controller can
/// report rather than a state that cannot be expressed.</para>
///
/// <para><b>Not persisted.</b> The request drops on a restart exactly as the mode does. That is
/// consistent with every other runtime setting here, and it is the one whose loss is discovered at
/// 05:00 with a flat car — which is why it is said out loud in the README rather than left implied.</para>
/// </summary>
public interface ITargetedChargeSelector
{
    /// <summary>The active request, or null when none has been made.</summary>
    TargetedChargeRequest? Request { get; }

    /// <summary>Sets the request. <paramref name="source"/> names who set it (for logging).</summary>
    void Set(TargetedChargeRequest request, string source);

    /// <summary>Clears the request. No-op (and no event) when there was none.</summary>
    void Clear(string source);

    /// <summary>Raised when the request actually changes, with the new value (null when cleared).</summary>
    event Action<TargetedChargeRequest?>? Changed;
}
