using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Providers.Testing;

/// <summary>A reading the hub refused, with why.</summary>
public sealed record RefusedReading(ChargerReading Reading, IReadOnlyList<string> Problems);
