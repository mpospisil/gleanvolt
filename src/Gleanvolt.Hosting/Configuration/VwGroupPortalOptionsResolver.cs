using Microsoft.Extensions.Configuration;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Builds the VW portal client's settings from configuration, accepting <b>both</b> the sectioned
/// form and the shorter plain names an owner actually types into a .env.
///
/// <para><c>Vehicle:DataAct:*</c> is the proper form and what a deployment should use. The
/// <c>VW_*</c> environment variables are honoured beside it because a hand-edited <c>.env</c> is
/// where these get typed, and four short names are easier to get right than four sectioned ones.</para>
///
/// <para>The section wins where both are present, because a deployment's own configuration should
/// not be quietly overridden by a developer's leftover <c>.env</c>.</para>
/// </summary>
public static class VwGroupPortalOptionsResolver
{
    /// <summary>The configuration section, on the same terms as every other feature's.</summary>
    public const string SectionName = "Vehicle:DataAct";

    /// <summary>
    /// Whether the portal is allowed to be read <b>on a clock</b> — <c>Vehicle:DataAct:Enabled</c>, or
    /// <c>VW_ENABLED</c> beside it.
    ///
    /// <para><b>Off by default, like everything that leaves the LAN</b>, and separate from having
    /// credentials on purpose: a <c>.env</c> that carries a VW ID because somebody pressed the
    /// <i>Vehicle portal</i> button once must not become an unattended feed at the next restart.
    /// Signing in on a schedule with an owner's real password is a decision, and this is where it is
    /// taken.</para>
    ///
    /// <para>It does not gate the button. The on-demand reader stays available whenever credentials
    /// are configured, which is what proves them before this is switched on.</para>
    /// </summary>
    public static bool IsFeedEnabled(IConfiguration configuration)
    {
        var stated = Pick(configuration.GetSection(SectionName)["Enabled"], "VW_ENABLED");

        return bool.TryParse(stated, out var enabled) && enabled;
    }

    public static VwGroupPortalOptions Resolve(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var defaults = new VwGroupPortalOptions();

        return new VwGroupPortalOptions
        {
            Brand = Pick(section["Brand"], "VW_BRAND"),
            ClientId = Pick(section["ClientId"], "VW_CLIENT_ID"),
            Username = Pick(section["Username"], "VW_USERNAME"),
            Password = Pick(section["Password"], "VW_PASSWORD"),
            Vin = Pick(section["Vin"], "VW_VIN"),

            // Overridable so the whole chain can be pointed at a stub; against the portal itself
            // neither is ever set.
            PortalBaseUrl = Or(Pick(section["PortalBaseUrl"], "VW_PORTAL_BASE"), defaults.PortalBaseUrl),
            IdentityBaseUrl = Or(Pick(section["IdentityBaseUrl"], "VW_IDENTITY_BASE"), defaults.IdentityBaseUrl),
        };
    }

    private static string Pick(string? fromSection, string environmentName) =>
        !string.IsNullOrWhiteSpace(fromSection)
            ? fromSection.Trim()
            : Environment.GetEnvironmentVariable(environmentName)?.Trim() ?? string.Empty;

    private static string Or(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
