using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Reads and writes the owner's tariff table on Settings and the API. Unlike the installation editors,
/// a save applies at once: <see cref="ITariffSchedule"/> reads the table afresh after every save, so
/// there is nothing to restart.
/// </summary>
public interface ITariffEditor
{
    /// <summary>The table as saved, or <see cref="TariffTable.None"/> when there is none or it cannot be read.</summary>
    TariffTable Read();

    /// <summary>
    /// Checks <paramref name="table"/> with <see cref="Configuration.TariffRules"/> and, when it has no
    /// errors, stores it normalized: <c>HH:mm</c> times, each list sorted by start. Warnings are saved
    /// through and returned with the result.
    /// </summary>
    TariffSaveResult Save(TariffTable table);

    /// <summary>What every price is in: <c>Tariff:Currency</c>, an ISO 4217 code.</summary>
    string Currency { get; }

    /// <summary>Raised after a save has been written, so a surface showing the schedule can refresh.</summary>
    event Action? Saved;
}
