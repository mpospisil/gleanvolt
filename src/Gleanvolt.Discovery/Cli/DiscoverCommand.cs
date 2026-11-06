namespace Gleanvolt.Discovery.Cli;

/// <summary>
/// The device search on the command line: <c>gleanvolt discover</c>, and the standalone
/// <c>gleanvolt-discover</c>, which is now a thin wrapper around this (#303). Progress goes to
/// <c>error</c>, so <c>--json</c> on the output stays parseable.
/// </summary>
public static class DiscoverCommand
{
    /// <summary>Runs a scan; returns 0, 2 for bad arguments, or 130 when stopped early.</summary>
    /// <param name="name">What the tool was started as, for the help text.</param>
    /// <param name="scanner">For tests: the scanner to use with the parsed options.</param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        string name,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<DiscoveryOptions, DeviceScanner>? scanner = null,
        Func<NetworkSelection>? networks = null)
    {
        var (arguments, problem) = CliArguments.Parse(args);

        if (arguments is null)
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            return 2;
        }

        if (arguments.Help)
        {
            await output.WriteLineAsync(CliArguments.Usage(name)).ConfigureAwait(false);
            return 0;
        }

        ScanPlan plan;

        if (arguments.Hosts.Count > 0)
        {
            plan = ScanPlan.ForHosts(arguments.Hosts);
        }
        else if (arguments.Subnets.Count > 0)
        {
            plan = ScanPlan.ForSubnets(arguments.Subnets);
        }
        else
        {
            var selection = networks?.Invoke()
                ?? NetworkSelection.FromInterfaces(NetworkSelection.LocalInterfaces(), NetworkSelection.RunningInContainer());
            plan = ScanPlan.ForSubnets(selection.Subnets, selection.Warnings);
        }

        await error.WriteLineAsync(
            $"Scanning {plan.Hosts.Count} host(s) on {string.Join(", ", plan.Networks)}, ports "
            + $"{string.Join(",", arguments.Options.Ports)}. Ctrl+C stops and shows what was found.").ConfigureAwait(false);

        var report = await (scanner?.Invoke(arguments.Options) ?? new DeviceScanner(arguments.Options)).ScanAsync(
            plan,
            device => error.WriteLine(ReportWriter.FoundLine(device)),
            cancellationToken).ConfigureAwait(false);

        if (arguments.Json)
        {
            await output.WriteLineAsync(ReportWriter.ToJson(report)).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync().ConfigureAwait(false);
            ReportWriter.WriteTable(report, output);
        }

        return report.Cancelled ? 130 : 0;
    }
}
