using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The Solcast account on Settings (issue #286). Like the tariff and unlike the installation editors, a
/// save applies at once: the forecast service reads the account on every fetch, so there is nothing to
/// restart — and a restart would itself spend one of the day's calls.
/// </summary>
public interface ISolcastAccountEditor
{
    /// <summary>The account in use, where it came from, and how the last fetch went.</summary>
    SolcastAccountSettings Read();

    /// <summary>
    /// Stores the account and fetches the forecast with it, once: that fetch is the check, and its
    /// outcome is in <see cref="Read"/>'s status when this returns. An empty <paramref name="apiKey"/>
    /// keeps the key in use, so changing only the site does not mean pasting the key again.
    /// </summary>
    Task<SolcastAccountSaveResult> SaveAsync(string apiKey, string resourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the account the page stored. The environment's, when there is one, is in use again;
    /// when there is none, the held forecast is dropped too. Spends no call.
    /// </summary>
    void Remove();

    /// <summary>Raised after a save, a remove, or any fetch, so a page showing the status can redraw.</summary>
    event Action? Changed;
}
