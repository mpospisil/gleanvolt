using Gleanvolt.Providers.Contracts.Vehicle;

namespace Gleanvolt.Providers.Testing;

/// <summary>A car's reading Gleanvolt would refuse, and why.</summary>
public sealed record RefusedVehicleReading(VehicleReading Reading, IReadOnlyList<string> Problems);
