namespace Gleanvolt.Api.Contracts;

/// <summary>
/// The API carries base units only (issue #263): W, Wh, seconds, metres, Pa. Where the domain still
/// holds something else, it is converted here, at the edge, and never the other way round.
/// </summary>
internal static class BaseUnits
{
    /// <summary>
    /// An <see cref="Core.Models.EnergyInterval"/> figure in Wh. Intervals stay in kWh below the API
    /// as the analytics surface; rounded to a tenth of a watt-hour so the scaling adds no float noise.
    /// </summary>
    internal static double Wh(double kwh) => Math.Round(kwh * 1000, 1);

    /// <inheritdoc cref="Wh(double)"/>
    internal static double? Wh(double? kwh) => kwh is { } value ? Wh(value) : null;
}
