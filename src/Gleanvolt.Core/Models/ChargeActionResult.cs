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
public sealed record ChargeActionResult(bool Succeeded, string? Message)
{
    public static ChargeActionResult Success { get; } = new(true, null);

    public static ChargeActionResult Failed(string message) => new(false, message);
}
