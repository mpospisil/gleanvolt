using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>
/// Help when a plugin is missing (#470): what a warning means, in a tip, and where to go to fix it. The warning says
/// what is wrong; the tip says what that means; the link says how to fix it, in the manual or on the plugin's own
/// page. Install steps stay in the manual, since they differ by installation and would not fit a tip.
/// </summary>
/// <param name="Tip">What the warning means, in two or three sentences.</param>
/// <param name="LinkText">The link at the end of the tip.</param>
/// <param name="ManualPage">The manual page it opens, or null for <paramref name="LocalHref"/>.</param>
/// <param name="LocalHref">A page of Gleanvolt's own it opens instead, or null.</param>
/// <param name="Prominent">The link is shown beside the warning too, not only in the tip: the first-run case.</param>
public sealed record PluginHelp(string Tip, string LinkText, string? ManualPage, string? LocalHref, bool Prominent = false)
{
    /// <summary>Adding the charger's plugin, by installation.</summary>
    public const string ChargerPage = "setting-up/charger";

    /// <summary>What to do when Gleanvolt turns the charger's plugin away.</summary>
    public const string ChargerRefusedPage = "setting-up/charger-refused";

    /// <summary>Plugins in general: starting one, accepting it, Forget.</summary>
    public const string PluginsPage = "setting-up/plugins";

    /// <summary>The Charger section on Settings, where the plugin's page is shown.</summary>
    public const string ChargerSection = "/settings#charger-editor";

    /// <summary>The help for a charger Gleanvolt can't control, or null when it can.</summary>
    public static PluginHelp? ForCharger(ChargerAvailability? charger)
    {
        if (charger is null || charger.Available)
        {
            return null;
        }

        // The phrase names the reason exactly; the state is the fallback for a reason worded without one.
        return (charger.ReasonPhrase?.Key, charger.State) switch
        {
            ("Charger.NoPlugin", _) or (null, ChargerAvailabilityState.NoPlugin) =>
                new PluginHelp(SharedText.PluginHelpNoPluginTip, SharedText.PluginHelpNoPluginLink, ChargerPage, null, Prominent: true),
            ("Charger.PluginRefused", _) =>
                new PluginHelp(SharedText.PluginHelpRefusedTip, SharedText.PluginHelpRefusedLink, ChargerRefusedPage, null),
            _ => new PluginHelp(SharedText.PluginHelpProblemTip, SharedText.PluginHelpProblemLink, null, ChargerSection),
        };
    }

    /// <summary>The help for a kind whose plugin is not connected, or null when it is, or the kind is off.</summary>
    /// <remarks>
    /// The charger's link is shown beside the state too: until its plugin connects, Gleanvolt can't charge.
    /// </remarks>
    public static PluginHelp? ForKind(ExternalProviderStatus status) => status.State switch
    {
        // A charger plugin turned away waits too, with the refusal beside it (#440).
        ExternalProviderState.Waiting when status.Kind == "charger" && status.LastRefusal is not null =>
            new PluginHelp(SharedText.PluginHelpRefusedTip, SharedText.PluginHelpRefusedLink, ChargerRefusedPage, null),
        ExternalProviderState.Waiting when status.Kind == "charger" =>
            new PluginHelp(SharedText.PluginHelpNoPluginTip, SharedText.PluginHelpNoPluginLink, ChargerPage, null, Prominent: true),
        ExternalProviderState.Waiting => new PluginHelp(SharedText.PluginHelpWaitingTip, SharedText.PluginHelpStartLink, Page(status.Kind), null),
        ExternalProviderState.Disconnected => new PluginHelp(SharedText.PluginHelpDisconnectedTip, SharedText.PluginHelpStartLink, Page(status.Kind), null),
        _ => null,
    };

    // The page that says how to start this kind's plugin: its own where there is one, the general page otherwise.
    private static string Page(string kind) => kind switch
    {
        "charger" => ChargerPage,
        "weather" => "setting-up/weather",
        _ => PluginsPage,
    };
}
