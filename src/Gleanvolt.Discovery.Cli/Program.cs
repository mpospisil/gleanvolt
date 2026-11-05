using Gleanvolt.Discovery.Cli;

// gleanvolt-discover: the standalone device search, for a laptop with no Gleanvolt installed. The
// command itself lives in Gleanvolt.Discovery, shared with `gleanvolt discover` (#303).
using var stop = new CancellationTokenSource();

// The first Ctrl+C stops the scan and keeps what it found; the process exits normally, with the
// report, rather than being killed mid-write.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

return await DiscoverCommand.RunAsync(args, "gleanvolt-discover", Console.Out, Console.Error, stop.Token);
