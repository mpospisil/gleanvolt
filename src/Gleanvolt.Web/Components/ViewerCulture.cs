using System.Globalization;

namespace Gleanvolt.Web.Components;

/// <summary>
/// The viewer's culture, captured when a component starts on its circuit, for the renders an event
/// raised somewhere else asks for (#290).
///
/// <para>A component that follows the poll re-renders from <c>InvokeAsync</c> called on the polling
/// loop's thread, and the work runs in that thread's execution context: invariant, because the
/// services are. Without this, a page opened in Czech stays Czech exactly until the first poll lands
/// and then quietly turns English. Measured, not guessed: a live Health page in the pseudo-locale
/// read plain English eight seconds after it opened.</para>
///
/// <para>The culture is set inside the dispatched work, never on the calling thread: the setting lives
/// in the execution context the work runs under, which is discarded when it returns, so the polling
/// loop itself stays invariant.</para>
/// </summary>
internal sealed class ViewerCulture
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    /// <summary><paramref name="work"/>, run in the viewer's culture.</summary>
    public Action Wrap(Action work) => () =>
    {
        Apply();
        work();
    };

    /// <inheritdoc cref="Wrap(Action)"/>
    public Func<Task> Wrap(Func<Task> work) => () =>
    {
        Apply();
        return work();
    };

    private void Apply()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}
