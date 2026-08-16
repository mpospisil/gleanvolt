using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

public interface IEnergyStateReader
{
    Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default);
}
