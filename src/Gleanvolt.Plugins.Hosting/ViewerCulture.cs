using System.Globalization;

namespace Gleanvolt.Plugins;

/// <summary>
/// The viewer's culture, captured when a component starts on its circuit, for the renders an event raised
/// somewhere else asks for. Keep one in a field and pass <c>InvokeAsync(_viewer.Wrap(StateHasChanged))</c>.
///
/// <para>A page that follows the charger re-renders from <c>InvokeAsync</c> called on the poll's thread,
/// and the work runs in that thread's execution context: the service's culture, not the viewer's. Without
/// this, a page opened in Czech stays Czech exactly until the first reading lands and then turns English.
/// Gleanvolt's own pages had the same bug and the same fix (Gleanvolt.Web's ViewerCulture, #290).</para>
///
/// <para>The culture is set inside the dispatched work, never on the calling thread: the setting lives in the
/// execution context the work runs under, which is discarded when it returns, so the poll stays as it was.</para>
/// </summary>
public sealed class ViewerCulture
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
