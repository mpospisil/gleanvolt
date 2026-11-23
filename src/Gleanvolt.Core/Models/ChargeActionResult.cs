namespace Gleanvolt.Core.Models;

/// <summary>
/// What came of a charge action. Actions can fail where selecting a mode never could: they write to
/// the charger, and a charger that doesn't answer must produce a sentence on the page rather than a
/// mode that quietly does nothing.
/// </summary>
/// <param name="Succeeded">Whether the charger accepted the use-mode write.</param>
/// <param name="Message">
/// Why it didn't, in words a dashboard can show. Null on success.
/// </param>
public sealed record ChargeActionResult(bool Succeeded, string? Message, Phrase? MessagePhrase = null)
{
    public static ChargeActionResult Success { get; } = new(true, null);

    /// <summary>
    /// True when nothing was tried because the charger is unavailable (#441): a conflict with the charger's
    /// state rather than a write it refused. The API answers it with 409.
    /// </summary>
    public bool ChargerUnavailable { get; init; }

    public static ChargeActionResult Failed(string message, Phrase? phrase = null) => new(false, message, phrase);

    /// <summary>Not started: the charger is unavailable, for <paramref name="availability"/>'s reason.</summary>
    public static ChargeActionResult Unavailable(ChargerAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var reason = availability.Reason ?? "the charger is unavailable";
        return new(
            false,
            $"No mode can start while Gleanvolt can't control the charger: {reason}",
            Phrase.Of("Action.ChargerUnavailable", new PhraseArg.Said(availability.ReasonPhrase, reason)))
        {
            ChargerUnavailable = true,
        };
    }
}
