namespace Gleanvolt.Plugin.Volkswagen.Volkswagen;

/// <summary>
/// What the plugin reads from its configuration (#410), under <c>Volkswagen</c>. The account itself (VW
/// ID, password, VIN) is not here: the owner enters it on the plugin's page and it is kept in
/// <see cref="DataDirectory"/>.
///
/// <para>Copied from the built-in <c>VwWebsiteOptions</c>. Changed: no <c>Enabled</c> (running the plugin is
/// the switch), and no <c>Username</c>, <c>Password</c> or <c>Vin</c> (see <see cref="VolkswagenAccount"/>).</para>
/// </summary>
public sealed class VolkswagenOptions
{
    public const string SectionName = "Volkswagen";

    public string PortalBaseUrl { get; set; } = "https://www.volkswagen.de";

    public string IdentityBaseUrl { get; set; } = "https://identity.vwgroup.io";

    /// <summary>
    /// Where the account and the signed-in session are kept: a volume in a container. Both are
    /// passwords; see README.md for what protects them.
    /// </summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// How often to ask while the car says it is charging. The built-in asks Gleanvolt's charge control
    /// whether a session is open; a plugin can't see that, so the car's own last charge state decides.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to ask otherwise. A parked car's SOC barely moves.</summary>
    public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The shortest gap between two reads of VW: a read asked for sooner (a reconnect, <i>Ask the car</i>
    /// twice) answers with the last reading instead. New in the plugin: the built-in had only its clock.
    /// </summary>
    public TimeSpan MinimumTimeBetweenReads { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The floor between credential re-logins. A broken session must never hammer VW's login: that is
    /// how an account gets locked.
    /// </summary>
    public TimeSpan MinimumTimeBetweenLogins { get; set; } = TimeSpan.FromMinutes(5);
}
