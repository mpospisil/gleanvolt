namespace Gleanvolt.Core.Models;

/// <summary>
/// Holds the latest <see cref="ChargeControlStatus"/> so a reporting layer can publish it without
/// being coupled to the polling loop. Singleton.
///
/// <para>It lives in Core rather than beside the poll loop because it has more than one consumer:
/// the Home Assistant MQTT worker, the session recorder, and the self-hosted web UI, which sits in
/// its own assembly and must not depend on the host. A plain holder with an event has no framework
/// dependency of any kind, so Core's layering rules are satisfied.</para>
/// </summary>
public sealed class ChargeControlStatusHolder
{
    private volatile ChargeControlStatus? _current;

    /// <summary>The most recent status, or null before the first poll.</summary>
    public ChargeControlStatus? Current => _current;

    /// <summary>Raised whenever a new status is set.</summary>
    public event Action<ChargeControlStatus>? Updated;

    public void Set(ChargeControlStatus status)
    {
        _current = status;
        Updated?.Invoke(status);
    }
}
