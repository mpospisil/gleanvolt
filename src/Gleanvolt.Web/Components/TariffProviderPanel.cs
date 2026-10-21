using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>
/// Which Settings panel edits a tariff strategy's own settings (issue #265): registered in DI beside
/// the strategy, so a new one brings its panel without <see cref="TariffEditorForm"/> changing. A
/// strategy with no registration has nothing to set on the page.
/// </summary>
/// <param name="ProviderId">The <see cref="Core.Interfaces.ITariffProvider.Id"/> it edits.</param>
/// <param name="Component">A <see cref="TariffProviderPanel"/>.</param>
public sealed record TariffPanelRegistration(string ProviderId, Type Component);

/// <summary>
/// What every strategy's panel is given and hands back. The form owns the save, the shared prices and
/// the checking; a panel owns only how its section of <c>tariff.json</c> is edited and shown.
/// </summary>
public abstract class TariffProviderPanel : ComponentBase
{
    /// <summary>The section as saved (undefined when there is none), read once when the panel opens.</summary>
    [Parameter] public JsonElement Settings { get; set; }

    /// <summary>Called with the whole section after every edit.</summary>
    [Parameter] public EventCallback<JsonElement> SettingsChanged { get; set; }

    /// <summary>The strategy's problems with what the panel last handed back, for showing in place.</summary>
    [Parameter] public IReadOnlyList<TariffProblem> Problems { get; set; } = [];

    /// <summary>True when the page has no login: show, but do not offer to change.</summary>
    [Parameter] public bool ReadOnly { get; set; }
}
