using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Targeting;

/// <summary>
/// Thread-safe holder for the active <see cref="TargetedChargeRequest"/> (see
/// <see cref="ITargetedChargeSelector"/>), registered as a singleton and started empty.
///
/// <para><b>Not persisted across restarts</b>, exactly like the mode selector beside it. The reasoning
/// is the same — after a crash, a power cut or a deploy the charger is left as its owner set it rather
/// than being grabbed by whatever was in a file — but the consequence is sharper here, because a
/// dropped request is discovered at 05:00 with a flat car. It is documented in the README for that
/// reason.</para>
/// </summary>
public sealed class TargetedChargeSelector : ITargetedChargeSelector
{
    private readonly ILogger<TargetedChargeSelector> _logger;
    private readonly Lock _gate = new();
    private TargetedChargeRequest? _request;

    public TargetedChargeSelector(ILogger<TargetedChargeSelector> logger) => _logger = logger;

    public TargetedChargeRequest? Request
    {
        get { lock (_gate) { return _request; } }
    }

    public event Action<TargetedChargeRequest?>? Changed;

    public void Set(TargetedChargeRequest request, string source)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_request == request)
            {
                return;
            }

            _request = request;
        }

        _logger.LogInformation(
            "Targeted charge request set by {Source}: {EnergyKWh:F1}kWh by {DepartBy}.",
            source,
            request.RequiredEnergyWh / 1000,
            request.DepartBy.LocalDateTime);

        Changed?.Invoke(request);
    }

    public void Clear(string source)
    {
        lock (_gate)
        {
            if (_request is null)
            {
                return;
            }

            _request = null;
        }

        _logger.LogInformation("Targeted charge request cleared by {Source}.", source);
        Changed?.Invoke(null);
    }
}
