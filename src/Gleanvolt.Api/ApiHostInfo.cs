namespace Gleanvolt.Api;

/// <summary>
/// The values the API has to report but cannot read for itself, handed over by the host.
///
/// <para>Same arrangement the web UI has with <c>WebBuildInfo</c> and for the same reason: the version
/// is stamped on the host's assembly and <c>Vehicle:MaxAge</c> is bound in <c>Gleanvolt.Hosting</c>,
/// an assembly this one must not reference. The host knows both and passes them; the surface does not
/// go looking.</para>
/// </summary>
/// <param name="Version">What <c>/health</c> reports as the running build.</param>
/// <param name="VehicleMaxAge">
/// How old a vehicle reading may be before <c>/vehicle</c> reports it as stale. The API reports the
/// age either way — see the endpoint for why a bare state of charge is a trap for a caller that
/// cannot see the clock.
/// </param>
public sealed record ApiHostInfo(string Version, TimeSpan VehicleMaxAge);
