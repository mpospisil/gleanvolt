namespace Gleanvolt.Web;

/// <summary>
/// The one piece of vehicle configuration the UI needs: how old a reading may be before it is shown as
/// stale.
///
/// <para>Same arrangement as <see cref="WebBuildInfo"/>, and for the same reason — the
/// <c>"Vehicle"</c> section is bound in <c>Gleanvolt.Hosting</c>, an assembly this one must not
/// reference, so the host hands the UI the single value it has to render rather than the whole options
/// object.</para>
/// </summary>
public sealed record VehicleDisplayOptions(TimeSpan MaxAge);
