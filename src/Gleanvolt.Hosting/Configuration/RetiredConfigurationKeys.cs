using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration keys that used to mean something and no longer do (issue #111).
///
/// <para><b>A key that has moved is a startup failure, not a silent ignore.</b> Every one of these
/// decided something real — which inverter to talk to, where the site is, how often to poll — and a
/// build that quietly ignored the old spelling would run against defaults while the operator's file
/// says otherwise. That failure mode is invisible: the controller starts, polls, charges, and is
/// simply pointed somewhere else. One refusal naming the replacement costs a restart and nothing
/// else.</para>
///
/// <para>The check is on <i>presence</i>, wherever configuration comes from — an environment
/// variable, a <c>.env</c> file, <c>appsettings.json</c>. It is deliberately not clever about what
/// the value is: a key that is set at all is a key someone believes in.</para>
/// </summary>
public static class RetiredConfigurationKeys
{
    /// <summary>
    /// What moved where. Sections are named by their section path — <c>Solax:Inverter</c> catches
    /// <c>Solax__Inverter__Host</c> and every sibling under it in one entry.
    /// </summary>
    private static readonly (string Retired, string Replacement)[] Moved =
    [
        ("Solax:Inverter", "Pv:Inverter"),
        ("Solax:EvCharger", "Pv:Chargers:0"),
        ("Solax:PollIntervalSeconds", "Controller:PollIntervalSeconds"),
        ("Weather:Latitude", "Pv:Latitude"),
        ("Weather:Longitude", "Pv:Longitude"),
    ];

    /// <summary>
    /// Stops the host if any retired key is still set.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// One or more retired keys are set. The message lists each with the key that replaces it, in both
    /// spellings, because the one an operator has to edit is usually the environment-variable form.
    /// </exception>
    public static void Refuse(IConfiguration configuration)
    {
        var found = Moved
            .Where(moved => IsSet(configuration, moved.Retired))
            .Select(moved =>
                $"  - {moved.Retired} has moved to {moved.Replacement} "
                + $"({EnvironmentVariable(moved.Retired)} -> {EnvironmentVariable(moved.Replacement)})")
            .ToList();

        if (found.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Configuration keys that no longer exist are still set:" + Environment.NewLine
            + string.Join(Environment.NewLine, found) + Environment.NewLine
            + "Move each value to its replacement and remove the old key.");
    }

    // A section counts as set when it has a value of its own or any child that does. GetSection().Exists()
    // is exactly that test, and it is what makes one entry cover a whole device block.
    private static bool IsSet(IConfiguration configuration, string key) => configuration.GetSection(key).Exists();

    private static string EnvironmentVariable(string key) => key.Replace(':', '_').Replace("_", "__");
}
