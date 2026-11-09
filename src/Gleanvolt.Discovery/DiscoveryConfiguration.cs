using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Discovery;

/// <summary>The configuration that would use what a scan found, ready to paste into <c>.env</c>.</summary>
public static class DiscoveryConfiguration
{
    /// <summary>
    /// <c>Pv__Inverter__Host=…</c> and the rest, with the driver where it is not SolaX, for the first
    /// inverter and the first charger in <paramref name="devices"/> that Gleanvolt can drive. Empty when there is neither. The variable
    /// names come from <see cref="PvSystemSettingKeys.EnvironmentVariable"/>, the same as
    /// <c>/pv-system</c> shows.
    /// </summary>
    public static IReadOnlyList<string> EnvironmentLines(IEnumerable<DiscoveredDevice> devices)
    {
        var usable = devices
            .Where(device => device is { Identified: true, Role: not null, Tier: not null }
                && (device.UnitId is not null || device.Finding == DiscoveryFinding.GoeCharger)
                && device.Tier is not SupportTier.NotSupported)
            .ToList();

        var lines = new List<string>();

        if (usable.FirstOrDefault(device => device.Role == PvDeviceRole.Inverter) is { } inverter)
        {
            // The driver only when it is not the default, as a configuration written by hand would have it.
            if (inverter.Driver is { } driver && driver != PvSystemRules.DefaultInverterDriver)
            {
                lines.Add($"{PvSystemSettingKeys.EnvironmentVariable(PvSystemSettingKeys.InverterDriver)}={driver}");
            }

            if (inverter.Topology is { } topology)
            {
                lines.Add($"{PvSystemSettingKeys.EnvironmentVariable(PvSystemSettingKeys.InverterTopology)}={topology}");
            }

            lines.AddRange(Lines(inverter, PvSystemSettingKeys.InverterModel, PvSystemSettingKeys.InverterHost,
                PvSystemSettingKeys.InverterPort, PvSystemSettingKeys.InverterUnitId));
        }

        if (usable.FirstOrDefault(device => device.Role == PvDeviceRole.Charger) is { } charger)
        {
            if (charger.Driver is { } driver && driver != PvSystemRules.DefaultChargerDriver)
            {
                lines.Add($"{PvSystemSettingKeys.EnvironmentVariable(PvSystemSettingKeys.ChargerDriver)}={driver}");
            }

            lines.AddRange(Lines(charger, PvSystemSettingKeys.ChargerModel, PvSystemSettingKeys.ChargerHost,
                PvSystemSettingKeys.ChargerPort, PvSystemSettingKeys.ChargerUnitId));
        }

        return lines;
    }

    private static IEnumerable<string> Lines(DiscoveredDevice device, string model, string host, string port, string unitId)
    {
        // go-e's model already says the brand.
        var brand = device.Finding switch
        {
            DiscoveryFinding.HuaweiDevice => "Huawei ",
            DiscoveryFinding.GoeCharger => string.Empty,
            _ => "SolaX ",
        };
        yield return $"{PvSystemSettingKeys.EnvironmentVariable(model)}={brand}{device.Model}";
        yield return $"{PvSystemSettingKeys.EnvironmentVariable(host)}={device.Host}";
        yield return $"{PvSystemSettingKeys.EnvironmentVariable(port)}={device.Port}";

        // An HTTP charger has no unit id.
        if (device.UnitId is { } unit)
        {
            yield return $"{PvSystemSettingKeys.EnvironmentVariable(unitId)}={unit}";
        }
    }
}
