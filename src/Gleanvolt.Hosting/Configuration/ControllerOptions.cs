namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Host-level settings that belong to no single feature. Bound from the <c>"Controller"</c> section.
/// </summary>
public sealed class ControllerOptions
{
    public const string SectionName = "Controller";

    /// <summary>
    /// The zone every "local" decision is made in: the forecast day boundary, the daily loan budget
    /// reset, and the zone id stamped onto each recorded charging session. Empty means "ask the
    /// operating system", i.e. <see cref="TimeZoneInfo.Local"/>.
    ///
    /// <para>Empty is correct on Linux, where the container's <c>TZ</c> environment variable already
    /// sets it — that is what <c>deploy/docker-compose.yml</c> does. It is <b>not</b> correct on
    /// Windows: .NET on Windows does not read <c>TZ</c> at all, so a Windows container silently runs
    /// in UTC no matter what the compose file says, and every session is recorded against the wrong
    /// day. The Windows image therefore sets this key explicitly.</para>
    ///
    /// <para>Accepts either an IANA id ("Europe/Prague") or a Windows id ("Central Europe Standard
    /// Time"); .NET 6+ resolves both on either platform, provided ICU is present. An id that cannot
    /// be resolved is a startup failure rather than a silent fall back to UTC — being quietly wrong
    /// about the day is precisely the bug this setting exists to prevent.</para>
    /// </summary>
    public string TimeZone { get; init; } = string.Empty;
}
