using Gleanvolt.Discovery;
using Gleanvolt.Discovery.Cli;

var (arguments, error) = CliArguments.Parse(args);

if (arguments is null)
{
    Console.Error.WriteLine(error);
    return 2;
}

if (arguments.Help)
{
    Console.WriteLine(CliArguments.Usage);
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
    var selection = NetworkSelection.FromInterfaces(NetworkSelection.LocalInterfaces(), NetworkSelection.RunningInContainer());
    plan = ScanPlan.ForSubnets(selection.Subnets, selection.Warnings);
}

using var stop = new CancellationTokenSource();

// The first Ctrl+C stops the scan and keeps what it found; the process exits normally, with the
// report, rather than being killed mid-write.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

// Progress goes to stderr, so --json on stdout stays parseable.
Console.Error.WriteLine(
    $"Scanning {plan.Hosts.Count} host(s) on {string.Join(", ", plan.Networks)}, ports "
    + $"{string.Join(",", arguments.Options.Ports)}. Ctrl+C stops and shows what was found.");

var report = await new DeviceScanner(arguments.Options).ScanAsync(
    plan,
    device => Console.Error.WriteLine(ReportWriter.FoundLine(device)),
    stop.Token);

if (arguments.Json)
{
    Console.WriteLine(ReportWriter.ToJson(report));
}
else
{
    Console.WriteLine();
    ReportWriter.WriteTable(report, Console.Out);
}

return report.Cancelled ? 130 : 0;
