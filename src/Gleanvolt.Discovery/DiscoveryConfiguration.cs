using Gleanvolt.Core.Models;

namespace Gleanvolt.Discovery;

/// <summary>The configuration that would use what a scan found, ready to paste into <c>.env</c>.</summary>
public static class DiscoveryConfiguration
{
    /// <summary>
    /// <c>Pv__Inverter__Host=…</c> and the rest, for the first inverter and the first charger in
    /// <paramref name="devices"/> that Gleanvolt can drive. Empty when there is neither. The variable
    /// names come from <see cref="PvSystemSettingKeys.EnvironmentVariable"/>, the same as
    /// <c>/pv-system</c> shows.
    /// </summary>
    public static IReadOnlyList<string> EnvironmentLines(IEnumerable<DiscoveredDevice> devices)
    {
        var usable = devices
            .Where(device => device is { Finding: DiscoveryFinding.SolaxDevice, Role: not null, UnitId: not null }
                && device.Tier is not SupportTier.NotSupported)
            .ToList();

        var lines = new List<string>();

        if (usable.FirstOrDefault(device => device.Role == PvDeviceRole.Inverter) is { } inverter)
        {
            lines.AddRange(Lines(inverter, PvSystemSettingKeys.InverterModel, PvSystemSettingKeys.InverterHost,
                PvSystemSettingKeys.InverterPort, PvSystemSettingKeys.InverterUnitId));
        }

        if (usable.FirstOrDefault(device => device.Role == PvDeviceRole.Charger) is { } charger)
        {
            lines.AddRange(Lines(charger, PvSystemSettingKeys.ChargerModel, PvSystemSettingKeys.ChargerHost,
                PvSystemSettingKeys.ChargerPort, PvSystemSettingKeys.ChargerUnitId));
        }

        return lines;
    }

    private static IEnumerable<string> Lines(DiscoveredDevice device, string model, string host, string port, string unitId)
    {
        yield return $"{PvSystemSettingKeys.EnvironmentVariable(model)}=SolaX {device.Model}";
        yield return $"{PvSystemSettingKeys.EnvironmentVariable(host)}={device.Host}";
        yield return $"{PvSystemSettingKeys.EnvironmentVariable(port)}={device.Port}";
        yield return $"{PvSystemSettingKeys.EnvironmentVariable(unitId)}={device.UnitId}";
    }
}
