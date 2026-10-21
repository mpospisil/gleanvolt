using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gleanvolt.Discovery.Cli;

/// <summary>A scan's report as a table, or as JSON.</summary>
internal static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(DiscoveryReport report) => JsonSerializer.Serialize(report, Json);

    /// <summary>
    /// The progress line printed the moment a device answers, so a scan that is stopped early (or a
    /// long one still running) has already said what each device is, not only that something is there.
    /// </summary>
    public static string FoundLine(DiscoveredDevice device)
    {
        var details = new[]
        {
            device.Role?.ToString().ToLowerInvariant(),
            device.Model,
            device.SerialNumber is null ? null : $"serial {device.SerialNumber}",
        }.OfType<string>().ToList();

        return $"  found {device.Host}:{device.Port} {(details.Count > 0 ? string.Join(", ", details) : Tier(device))}";
    }

    public static void WriteTable(DiscoveryReport report, TextWriter output)
    {
        foreach (var warning in report.Warnings)
        {
            output.WriteLine($"! {warning}");
        }

        if (report.Warnings.Count > 0)
        {
            output.WriteLine();
        }

        if (report.Cancelled)
        {
            output.WriteLine("Stopped before the scan finished; this is what it had found so far.");
            output.WriteLine();
        }

        if (report.Devices.Count == 0)
        {
            output.WriteLine($"Nothing answered on {string.Join(", ", report.Networks)} ({report.HostsScanned} hosts).");
            output.WriteLine("Is Modbus TCP enabled in the inverter's own menu? A PocketLAN dongle does not provide it.");
            return;
        }

        string[] header = ["Host", "Port", "Unit", "Role", "Model", "Serial", "Tier", "Note"];
        var rows = report.Devices.Select(device => new[]
        {
            device.Host,
            device.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            device.UnitId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            device.Role?.ToString().ToLowerInvariant() ?? "-",
            device.Model is null ? "-" : device.Family is null ? device.Model : $"{device.Model} ({device.Family})",
            device.SerialNumber ?? "-",
            Tier(device),
            device.Note,
        }).ToList();

        // Every column but the note sized to its widest cell; the note runs on, since it is the one a
        // reader needs whole.
        var widths = Enumerable.Range(0, header.Length - 1)
            .Select(column => rows.Select(row => row[column].Length).Append(header[column].Length).Max())
            .ToList();

        void Row(string[] cells) => output.WriteLine(
            string.Join("  ", cells.Take(widths.Count).Select((cell, column) => cell.PadRight(widths[column])))
            + "  " + cells[^1]);

        Row(header);
        Row([.. header.Select((_, column) => column < widths.Count ? new string('-', widths[column]) : "----")]);
        rows.ForEach(Row);

        var configuration = DiscoveryConfiguration.EnvironmentLines(report.Devices);

        if (configuration.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("To use these, put this in .env (or enter it on /pv-system):");
            output.WriteLine();

            foreach (var line in configuration)
            {
                output.WriteLine($"  {line}");
            }
        }

        if (report.Devices.Any(device => device.SerialNumber is not null))
        {
            output.WriteLine();
            output.WriteLine("Quoting a serial number in an issue? The first few characters are enough.");
        }
    }

    private static string Tier(DiscoveredDevice device) => device.Tier switch
    {
        SupportTier.Verified => "Verified",
        SupportTier.Expected => "Expected",
        SupportTier.ReadingsAndChargingOnly => "Readings and charging only",
        SupportTier.NotSupported => "Not supported",
        SupportTier.UnknownSolax => "Unknown SolaX",
        SupportTier.NotSolax => "Not a SolaX",
        _ => device.Finding switch
        {
            DiscoveryFinding.NoModbusReply => "Open, no Modbus reply",
            DiscoveryFinding.NoUnitAnswered => "Gateway, no device",
            DiscoveryFinding.SolaxDongle => "Dongle",
            _ => "-",
        },
    };
}
