using System.Globalization;
using System.Net;

namespace Gleanvolt.Discovery.Cli;

/// <summary>The command line, parsed. Hand-rolled: a dozen flags do not justify a dependency.</summary>
internal sealed record CliArguments
{
    public const string Usage = """
        gleanvolt-discover: find the SolaX inverter and EV charger on your network, and the Modbus TCP
        host, port and unit id each one answers on. Read-only: it never writes to a device.

        Stop Gleanvolt (and Home Assistant's SolaX integration) first: a SolaX device accepts very few
        Modbus connections, and one already taken looks like "port open, no Modbus reply".

        Usage: gleanvolt-discover [options]

          --subnet <cidr>          Scan this subnet, e.g. 192.168.1.0/24. Repeatable. Default: every
                                   private IPv4 interface's subnet, each capped at a /22.
          --host <address>         Scan only this host. Repeatable.
          --ports <list>           Ports to try, comma-separated. Default: 502,8899,80.
          --unit-ids <list>        Modbus unit ids to try, in order. Default: 1,0,2,3,4,5,247.
          --all-unit-ids           Keep trying unit ids after one answers (two devices behind one bridge).
          --connect-timeout <ms>   Per TCP connect. Default: 1000.
          --request-timeout <ms>   Per Modbus request. Default: 2000.
          --host-timeout <s>       Per host, all ports and unit ids together. Default: 45.
          --concurrency <n>        Port probes in flight at once. Default: 64.
          --json                   Print the result as JSON instead of a table.
          -h, --help               Show this text.

        Ctrl+C stops the scan and prints what it has found so far.
        """;

    public List<Ipv4Subnet> Subnets { get; } = [];

    public List<string> Hosts { get; } = [];

    public bool Json { get; private set; }

    public bool Help { get; private set; }

    public DiscoveryOptions Options { get; private set; } = new();

    /// <summary>The arguments, or the reason they could not be read.</summary>
    public static (CliArguments? Arguments, string? Error) Parse(IReadOnlyList<string> args)
    {
        var parsed = new CliArguments();
        var options = new DiscoveryOptions();

        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];

            string Value()
            {
                if (i + 1 >= args.Count)
                {
                    throw new FormatException($"{flag} needs a value.");
                }

                return args[++i];
            }

            try
            {
                switch (flag)
                {
                    case "-h" or "--help":
                        parsed.Help = true;
                        break;
                    case "--json":
                        parsed.Json = true;
                        break;
                    case "--all-unit-ids":
                        options = options with { AllUnitIds = true };
                        break;
                    case "--subnet":
                        var subnet = Value();
                        parsed.Subnets.Add(Ipv4Subnet.TryParse(subnet, out var cidr) && subnet.Contains('/')
                            ? cidr.Value
                            : throw new FormatException($"--subnet {subnet} is not an IPv4 subnet such as 192.168.1.0/24."));
                        break;
                    case "--host":
                        var host = Value();
                        parsed.Hosts.Add(IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns
                            ? host
                            : throw new FormatException($"--host {host} is not an address or a host name."));
                        break;
                    case "--ports":
                        options = options with
                        {
                            Ports = Numbers(Value(), flag, 1, 65535),
                        };
                        break;
                    case "--unit-ids":
                        options = options with
                        {
                            UnitIds = [.. Numbers(Value(), flag, 0, 255).Select(number => (byte)number)],
                        };
                        break;
                    case "--connect-timeout":
                        options = options with { ConnectTimeout = TimeSpan.FromMilliseconds(Positive(Value(), flag)) };
                        break;
                    case "--request-timeout":
                        options = options with { RequestTimeout = TimeSpan.FromMilliseconds(Positive(Value(), flag)) };
                        break;
                    case "--host-timeout":
                        options = options with { HostTimeout = TimeSpan.FromSeconds(Positive(Value(), flag)) };
                        break;
                    case "--concurrency":
                        options = options with { MaxConcurrency = Positive(Value(), flag) };
                        break;
                    default:
                        return (null, $"Unknown option {flag}. See --help.");
                }
            }
            catch (FormatException ex)
            {
                return (null, ex.Message);
            }
        }

        if (parsed.Subnets.Count > 0 && parsed.Hosts.Count > 0)
        {
            return (null, "Pass --subnet or --host, not both.");
        }

        parsed.Options = options;
        return (parsed, null);
    }

    private static int Positive(string text, string flag) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new FormatException($"{flag} {text} is not a positive whole number.");

    private static List<int> Numbers(string text, string flag, int min, int max)
    {
        var items = new List<int>();

        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            {
                throw new FormatException($"{flag}: {part} is not a number from {min} to {max}.");
            }

            items.Add(number);
        }

        return items.Count > 0 ? [.. items.Distinct()] : throw new FormatException($"{flag} needs at least one value.");
    }
}
