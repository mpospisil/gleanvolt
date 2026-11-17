using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Resume;

namespace Gleanvolt.Hosting.Tariff;

/// <summary>
/// Thread-safe runtime <b>low tariff only</b> flags (see <see cref="ILowTariffSettings"/>). Registered as
/// a singleton and seeded from <see cref="FastChargeOptions"/> and <see cref="SolarGridChargeOptions"/>;
/// the configured values are only the defaults. A change made from the web UI, MQTT or the API is kept
/// in <see cref="ChargeSettingsFile"/> and survives a restart (#421), like
/// <see cref="SolarGrid.SolarGridSettings"/>.
/// </summary>
public sealed class LowTariffSettings : ILowTariffSettings
{
    private readonly ILogger<LowTariffSettings> _logger;
    private readonly ChargeSettingsFile? _file;
    private readonly Lock _gate = new();
    private readonly HashSet<ChargeControlMode> _on = [];

    /// <param name="fast">The configured flag for FastNoBattery, used until somebody changes it.</param>
    /// <param name="solarGrid">The configured flag for SolarGrid, likewise.</param>
    /// <param name="logger">Where changes are recorded.</param>
    /// <param name="file">Where changes are kept across restarts; null keeps them in memory only.</param>
    public LowTariffSettings(
        IOptions<FastChargeOptions> fast,
        IOptions<SolarGridChargeOptions> solarGrid,
        ILogger<LowTariffSettings> logger,
        ChargeSettingsFile? file = null)
    {
        _logger = logger;
        _file = file;
        var saved = file?.Saved ?? new SavedChargeSettings();

        Seed(ChargeControlMode.FastNoBattery, saved.FastNoBatteryLowTariffOnly, fast.Value.LowTariffOnly);
        Seed(ChargeControlMode.SolarGrid, saved.SolarGridLowTariffOnly, solarGrid.Value.LowTariffOnly);
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

            _file?.Update(saved => mode == ChargeControlMode.FastNoBattery
                ? saved with { FastNoBatteryLowTariffOnly = on }
                : saved with { SolarGridLowTariffOnly = on });
        }

        _logger.LogInformation("Low tariff only for {Mode} turned {State} by {Source}.", mode, on ? "on" : "off", source);
    }

    private void Seed(ChargeControlMode mode, bool? saved, bool configured)
    {
        if (saved ?? configured)
        {
            _on.Add(mode);
        }

        if (saved is { } kept)
        {
            _logger.LogInformation(
                "Low tariff only for {Mode} {State} as last set (configured {Configured}).",
                mode,
                kept ? "on" : "off",
                configured ? "on" : "off");
        }
    }
}
