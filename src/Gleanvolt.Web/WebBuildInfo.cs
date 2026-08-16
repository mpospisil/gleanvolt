namespace Gleanvolt.Web;

/// <summary>
/// The build the process is running, as the UI should display it (e.g. <c>1.2.0 (31bf347)</c>).
///
/// <para>Handed in by the host rather than read here: the version is stamped on the entry assembly
/// and the host is the only assembly that knows how this deployment describes itself. Keeping it a
/// registered value also means a test can render the page against a fixed version string.</para>
/// </summary>
/// <param name="Version">Version and, on a CI build, the abbreviated commit.</param>
public sealed record WebBuildInfo(string Version);
