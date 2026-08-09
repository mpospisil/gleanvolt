using System.Text.Json;
using System.Text.Json.Serialization;
using Solax.Core.Models;

namespace Solax.Infrastructure.Sessions;

/// <summary>
/// The JSON contract for stored plans and published session documents.
///
/// <para>The options are shared deliberately: the same shape that goes into the <c>start_plan_json</c>
/// column comes back out of it, and the same shape a future upload writes to object storage is what a
/// web app reads. Changing these settings changes a persisted format, so they belong in one place
/// rather than at each call site.</para>
/// </summary>
public static class ChargingSessionJson
{
    /// <summary>
    /// Enums as names (a stored <c>"Forecasted"</c> survives someone reordering the enum, an ordinal
    /// wouldn't), and fields included because <see cref="SolarDayPlan.NextFeasibleWindow"/> is a value
    /// tuple, whose members are fields rather than properties and would otherwise vanish silently.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Renders a session document as the JSON that gets published.</summary>
    public static string Serialize(ChargingSessionDocument document) =>
        JsonSerializer.Serialize(document, Options);

    /// <summary>Reads a session document back.</summary>
    public static ChargingSessionDocument? Deserialize(string json) =>
        JsonSerializer.Deserialize<ChargingSessionDocument>(json, Options);
}
