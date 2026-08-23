using Microsoft.AspNetCore.Http;

namespace Gleanvolt.Api.Endpoints;

/// <summary>
/// The failures every endpoint here can have, said the same way each time — RFC 9457 problem details,
/// which is what a generated client already knows how to read.
/// </summary>
internal static class ApiResults
{
    /// <summary>
    /// Nothing has been read from the hardware yet, so there is no honest answer. A distinct status
    /// from "broken": the service is up, it has simply not completed a poll — at startup, or while the
    /// inverter is unreachable. A caller should retry rather than conclude anything.
    /// </summary>
    internal static IResult NotPolled() => Results.Problem(
        title: "No telemetry yet",
        detail: "No poll has completed since the service started, so there is nothing to report. Retry shortly.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// A store could not be read: the feature is switched off, or its file could not be opened.
    /// Recording's own failures are silent by design, so browsing degrades the same way — this reports
    /// "not available", never a crash.
    /// </summary>
    internal static IResult StoreUnavailable(string what) => Results.Problem(
        title: "History not available",
        detail: $"The {what} could not be read. It is either disabled in configuration or its database "
            + "could not be opened; the controller keeps running either way.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>The caller asked for something the API will not do, with the reason in the caller's own terms.</summary>
    internal static IResult BadRequest(string detail) => Results.Problem(
        title: "Bad request",
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Nothing by that identifier.</summary>
    internal static IResult NotFound(string detail) => Results.Problem(
        title: "Not found",
        detail: detail,
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The range a history query resolved to, or the reason it is not allowed. Ranges are bounded
    /// deliberately: a caller will cheerfully ask for a year of quarter-hours, and this runs on a
    /// Raspberry Pi.
    /// </summary>
    internal static bool TryResolveRange(
        DateTimeOffset? from,
        DateTimeOffset? to,
        TimeSpan window,
        TimeSpan maxRange,
        DateTimeOffset now,
        out DateTimeOffset resolvedFrom,
        out DateTimeOffset resolvedTo,
        out IResult? error)
    {
        resolvedTo = to ?? now;
        resolvedFrom = from ?? resolvedTo - window;
        error = null;

        if (resolvedTo <= resolvedFrom)
        {
            error = BadRequest("'to' must be after 'from'.");
            return false;
        }

        if (resolvedTo - resolvedFrom > maxRange)
        {
            error = BadRequest(
                $"That range is {(resolvedTo - resolvedFrom).TotalDays:F0} days; the most one query may "
                + $"ask for is {maxRange.TotalDays:F0}. Ask for it in several requests.");
            return false;
        }

        return true;
    }
}
