using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using static System.FormattableString;

namespace Gleanvolt.Hosting.Fast;

/// <summary>
/// Thread-safe holder for the active <see cref="FastChargeLimit"/> (see
/// <see cref="IFastChargeSelector"/>), registered as a singleton and started empty — which is not an
/// empty state waiting to be filled but the <see cref="Core.Enums.FastChargeBasis.Full"/> case: charge
/// until the car says stop, exactly as the mode behaved before it could be given an amount.
///
/// <para>Held in memory only; a restart that resumes the fast charge puts the limit back (#413, see
/// <see cref="FastChargeProvider.Restore"/>).</para>
/// </summary>
public sealed class FastChargeSelector : IFastChargeSelector
{
    private readonly ILogger<FastChargeSelector> _logger;
    private readonly Lock _gate = new();
    private FastChargeLimit? _limit;

    public FastChargeSelector(ILogger<FastChargeSelector> logger) => _logger = logger;

    public FastChargeLimit? Limit
    {
        get { lock (_gate) { return _limit; } }
    }

    public event Action<FastChargeLimit?>? Changed;

    public void Set(FastChargeLimit limit, string source)
    {
        ArgumentNullException.ThrowIfNull(limit);

        lock (_gate)
        {
            if (_limit == limit)
            {
                return;
            }

            _limit = limit;
        }

        _logger.LogInformation(
            "Fast charge limit set by {Source}: {EnergyKWh:F1}kWh{AsSoc}.",
            source,
            limit.RequiredEnergyWh / 1000,
            limit.IsSocBased
                ? Invariant($" ({limit.VehicleSocPercentAtRequest:F0}% -> {limit.TargetSocPercent:F0}%)")
                : string.Empty);

        Changed?.Invoke(limit);
    }

    public void Clear(string source)
    {
        lock (_gate)
        {
            if (_limit is null)
            {
                return;
            }

            _limit = null;
        }

        _logger.LogInformation("Fast charge limit cleared by {Source}: charging until the car stops.", source);
        Changed?.Invoke(null);
    }
}
