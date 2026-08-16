using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Gleanvolt.Web;

internal static class CultureConfiguration
{
    // kWh/W/% figures are units, not localized text -- there is no translation story for this
    // dashboard, so pin formatting to invariant rather than inherit whatever locale the host OS
    // happens to run under (this broke on a cs-CZ machine: "6,00 kWh" instead of "6.00 kWh").
    //
    // CA2255 assumes a module initializer belongs in the entry assembly; this one deliberately
    // lives here instead, because Gleanvolt.Web is loaded by both the production host
    // (Gleanvolt.Worker) and the bUnit test host (Gleanvolt.Web.Tests), and both need the effect.
    [SuppressMessage("Usage", "CA2255", Justification = "Shared by both the production and test hosts that load this assembly.")]
    [ModuleInitializer]
    internal static void ForceInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
    }
}
