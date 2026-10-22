using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tariff;

/// <summary>
/// Thread-safe runtime <b>low tariff only</b> flags (see <see cref="ILowTariffSettings"/>). Registered as
/// a singleton and seeded from <see cref="FastChargeOptions"/> and <see cref="SolarGridChargeOptions"/>;
/// the configured values are only the boot defaults, and a change made from the web UI or Home Assistant
/// does not survive a restart — the same contract as <see cref="SolarGrid.SolarGridSettings"/>.
/// </summary>
public sealed class LowTariffSettings : ILowTariffSettings
{
    private readonly ILogger<LowTariffSettings> _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<ChargeControlMode> _on = [];

    public LowTariffSettings(
        IOptions<FastChargeOptions> fast,
        IOptions<SolarGridChargeOptions> solarGrid,
        ILogger<LowTariffSettings> logger)
    {
        _logger = logger;

        if (fast.Value.LowTariffOnly)
        {
            _on.Add(ChargeControlMode.FastNoBattery);
        }

        if (solarGrid.Value.LowTariffOnly)
        {
            _on.Add(ChargeControlMode.SolarGrid);
        }
    }

    public bool IsLowTariffOnly(ChargeControlMode mode)
    {
        lock (_gate)
        {
            return _on.Contains(mode);
        }
    }

    public void SetLowTariffOnly(ChargeControlMode mode, bool on, string source)
    {
        if (!LowTariffModes.Has(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only Fast and SolarGrid have a low-tariff-only option.");
        }

        lock (_gate)
        {
            if ((on ? _on.Add(mode) : _on.Remove(mode)) is false)
            {
                return;
            }
        }

        _logger.LogInformation("Low tariff only for {Mode} turned {State} by {Source}.", mode, on ? "on" : "off", source);
    }
}
