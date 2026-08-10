namespace Solax.Worker.Configuration;

/// <summary>
/// A <see cref="TimeProvider"/> that reports a configured <see cref="LocalTimeZone"/> instead of the
/// operating system's. Everything else — the clock, timers, timestamps — is delegated untouched to
/// <see cref="TimeProvider.System"/>.
///
/// <para>It exists because <see cref="TimeZoneInfo.Local"/> is not configurable in a container on
/// every platform: Linux honours <c>TZ</c>, Windows does not. Routing the zone through the
/// <see cref="TimeProvider"/> the services already take means the fix lands everywhere the day
/// boundary is computed, without a second notion of "local" for callers to pick the wrong one of.
/// </para>
/// </summary>
public sealed class ZonedTimeProvider : TimeProvider
{
    private readonly TimeProvider _inner;
    private readonly TimeZoneInfo _zone;

    public ZonedTimeProvider(TimeZoneInfo zone, TimeProvider? inner = null)
    {
        _zone = zone;
        _inner = inner ?? System;
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);

    /// <summary>
    /// Resolves <paramref name="timeZoneId"/> to a provider, or returns <see cref="TimeProvider.System"/>
    /// unchanged when the id is empty. Throws when the id is set but unknown: a typo that silently
    /// degraded to UTC would shift every day boundary and every recorded session by hours, and would
    /// show up only as inexplicably bad charging decisions days later.
    /// </summary>
    public static TimeProvider Resolve(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return System;
        }

        // Accepts IANA and Windows ids on both platforms (.NET 6+), so the same appsettings value
        // works in the Linux and the Nano Server image.
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        return new ZonedTimeProvider(zone);
    }
}
