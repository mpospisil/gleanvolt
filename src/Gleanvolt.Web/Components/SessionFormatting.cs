using Gleanvolt.Core.Models;

namespace Gleanvolt.Web.Components;

/// <summary>Shared between the session list and session detail pages, so the two never drift.</summary>
internal static class SessionFormatting
{
    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m";

    public static string FormatStrategy(this ChargingSession session) =>
        session.StartMode == session.EndMode
            ? session.StartMode.ToString()
            : $"{session.StartMode} → {session.EndMode}";
}
