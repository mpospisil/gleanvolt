namespace Gleanvolt.Devices;

/// <summary>
/// A few values a driver must keep across a restart (issue #323): what a lasting setting was before the
/// driver changed it, so it can be put back even after a crash or a power cut. The host keeps it in a file
/// beside the other state; a driver never touches the disk itself.
///
/// <para>Small, and written rarely: once when a lasting setting is changed, once when it is put back.
/// Keys are the driver's own, prefixed with its id (<c>huawei:...</c>).</para>
/// </summary>
public interface IDriverStateStore
{
    /// <summary>The value under <paramref name="key"/>, or null when there is none.</summary>
    string? Get(string key);

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>, or removes the key when it is null, and
    /// does not return until it is durable. Throws when it cannot be stored: a driver must not change a
    /// lasting setting whose original it could not keep.
    /// </summary>
    void Set(string key, string? value);
}

/// <summary>
/// A <see cref="IDriverStateStore"/> that forgets everything when the process ends: for tests, and for a
/// driver created to probe or snapshot, which never changes a lasting setting.
/// </summary>
public sealed class InMemoryDriverStateStore : IDriverStateStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (_values)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    public void Set(string key, string? value)
    {
        lock (_values)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }
        }
    }
}
