namespace Gleanvolt.Web;

/// <summary>
/// What the host knows about <i>how</i> the installation was configured, as opposed to what it
/// resolved to: one message per deprecated key that is still supplying a value.
///
/// <para>The same arrangement <see cref="WebBuildInfo"/> has, and for the same reason. The list is
/// produced by the host's <c>PvSystemResolver</c> while the service collection is being built, and
/// <c>Gleanvolt.Hosting</c> is an assembly this one must not reference; the host knows and hands it
/// over rather than the UI going looking. Empty once a deployment has moved everything into the
/// <c>Pv</c> section.</para>
/// </summary>
/// <param name="Deprecations">The messages, in the order the resolver found them.</param>
public sealed record PvSystemNotices(IReadOnlyList<string> Deprecations);
