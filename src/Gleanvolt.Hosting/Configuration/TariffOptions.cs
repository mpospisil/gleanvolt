namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Configuration for the tariff (issue #265). Bound from the <c>"Tariff"</c> section. The table itself
/// is not configuration: it is the file at <see cref="Path"/>, edited on Settings.
/// </summary>
public sealed class TariffOptions
{
    public const string SectionName = "Tariff";

    /// <summary>
    /// The owner's table, relative to the content root unless absolute: <c>data/tariff.json</c>, beside
    /// <c>pv-system.json</c>, so it lands on the same bind mount in the container.
    /// </summary>
    public string Path { get; init; } = "data/tariff.json";

    /// <summary>
    /// What every price is in, as an ISO 4217 code. Configuration rather than a field on Settings: it
    /// labels the price fields and every price shown, and an installation's currency does not change.
    /// </summary>
    public string Currency { get; init; } = "CZK";
}
