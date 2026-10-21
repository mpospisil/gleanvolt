using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Reads and writes the tariff settings on Settings and the API (issue #265). Unlike the installation
/// editors, a save applies at once: <see cref="ITariffSchedule"/> asks the selected strategy afresh
/// after every save, so there is nothing to restart.
/// </summary>
public interface ITariffEditor
{
    /// <summary>The strategies this build has, in the order Settings offers them.</summary>
    IReadOnlyList<ITariffProvider> Providers { get; }

    /// <summary>What every price is in: <c>Tariff:Currency</c>, an ISO 4217 code.</summary>
    string Currency { get; }

    /// <summary>The settings as saved, or <see cref="TariffSettings.None"/> when there are none or they cannot be read.</summary>
    TariffSettings Read();

    /// <summary>
    /// Checks the shared prices and the selected strategy's own section and, when neither has an error,
    /// stores <paramref name="settings"/> with that section normalized by its strategy. Every other
    /// section is stored as given, including those of strategies this build does not have. Warnings
    /// are saved through and returned with the result.
    /// </summary>
    TariffSaveResult Save(TariffSettings settings);

    /// <summary>Every problem <see cref="Save"/> would find in <paramref name="settings"/>, without saving.</summary>
    IReadOnlyList<TariffProblem> Check(TariffSettings settings);

    /// <summary>Raised after a save has been written, so the schedule and a surface showing it can refresh.</summary>
    event Action? Saved;
}
