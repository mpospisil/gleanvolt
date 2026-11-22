using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gleanvolt.Plugin.Shelly.TopAc;

/// <summary>Why a call to the charger failed.</summary>
public enum RpcFailure
{
    /// <summary>No address is set yet.</summary>
    NoAddress,

    /// <summary>Nothing answered at the address.</summary>
    Unreachable,

    /// <summary>The charger asks for a password (401): v1 needs authentication off.</summary>
    Unauthorized,

    /// <summary>It answered, but not as a TopAC does: another device, or another firmware.</summary>
    NotTopAc,
}

/// <summary>A call to the charger failed.</summary>
public sealed class RpcFailedException(RpcFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public RpcFailure Failure { get; } = failure;
}

/// <summary>One phase of <c>phase_info</c>.</summary>
public sealed record PhaseInfo(double? Voltage, double? Current, double? Power);

/// <summary><c>phase_info</c>: the total power as the charger sends it (unit unknown, see <see cref="ShellyCharger"/>), and each phase.</summary>
public sealed record PowerInfo(double? TotalPower, IReadOnlyList<PhaseInfo> Phases);

/// <summary>The owner's own settings, <c>Service.GetConfig?id=0</c>.</summary>
public sealed record OwnerSettings(bool? AutoCharge, bool? AutoBalance);

/// <summary>
/// The TopAC's local Gen2 RPC API (#429): Shelly's documented virtual components under owner <c>service:0</c>,
/// each a GET of <c>/rpc/&lt;Method&gt;?owner="service:0"&amp;role="&lt;role&gt;"</c> that answers with the bare
/// result, <c>{"value": …}</c>. Sends no credentials: Shelly ships with authentication off, and v1 needs it off.
/// </summary>
public sealed class ShellyRpcClient
{
    /// <summary>The roles the plugin reads, in the order a read asks for them.</summary>
    public static IReadOnlyList<string> ReadRoles { get; } = ["work_state", "phase_info", "current_limit", "start_charging"];

    /// <summary>The only roles it writes: the setpoint and the start/stop switch.</summary>
    public static IReadOnlyList<string> WrittenRoles { get; } = ["current_limit", "start_charging"];

    private readonly HttpClient _http;
    private readonly ChargerAddressStore _address;
    private readonly ShellyOptions _options;

    public ShellyRpcClient(HttpClient http, ChargerAddressStore address, ShellyOptions options)
    {
        _http = http;
        _address = address;
        _options = options;
    }

    /// <summary>The path and query of a component call, relative to the charger, as a recording keys it.</summary>
    public static string ComponentCall(string method, string role, string? value = null) =>
        $"rpc/{method}?owner=%22service:0%22&role=%22{role}%22" + (value is null ? "" : "&value=" + value);

    /// <summary><c>work_state</c>: <c>charger_free</c>, <c>charger_charging</c>, …</summary>
    public async Task<string> GetWorkStateAsync(Uri? at = null, CancellationToken cancellationToken = default)
    {
        var answer = await CallAsync(ComponentCall("Enum.GetStatus", "work_state"), at, cancellationToken).ConfigureAwait(false);
        return answer["value"] is JsonValue value && value.TryGetValue<string>(out var state) && state.Length > 0
            ? state
            : throw new RpcFailedException(RpcFailure.NotTopAc, "The charger's work_state has no value.");
    }

    /// <summary><c>phase_info</c>.</summary>
    public async Task<PowerInfo> GetPowerAsync(CancellationToken cancellationToken = default)
    {
        var answer = await CallAsync(ComponentCall("Object.GetStatus", "phase_info"), null, cancellationToken).ConfigureAwait(false);
        if (answer["value"] is not JsonObject info)
        {
            throw new RpcFailedException(RpcFailure.NotTopAc, "The charger's phase_info has no value.");
        }

        var phases = new List<PhaseInfo>();
        foreach (var name in (string[])["phase_a", "phase_b", "phase_c"])
        {
            if (info[name] is JsonObject phase)
            {
                phases.Add(new PhaseInfo(Number(phase["voltage"]), Number(phase["current"]), Number(phase["power"])));
            }
        }

        return new PowerInfo(Number(info["total_power"]), phases);
    }

    /// <summary><c>current_limit</c>, in whole amps.</summary>
    public async Task<int> GetCurrentLimitAsync(CancellationToken cancellationToken = default)
    {
        var answer = await CallAsync(ComponentCall("Number.GetStatus", "current_limit"), null, cancellationToken).ConfigureAwait(false);
        return Number(answer["value"]) is { } amps
            ? (int)Math.Round(amps)
            : throw new RpcFailedException(RpcFailure.NotTopAc, "The charger's current_limit has no value.");
    }

    /// <summary><c>start_charging</c>.</summary>
    public async Task<bool> GetStartChargingAsync(CancellationToken cancellationToken = default)
    {
        var answer = await CallAsync(ComponentCall("Boolean.GetStatus", "start_charging"), null, cancellationToken).ConfigureAwait(false);
        return answer["value"] is JsonValue value && value.TryGetValue<bool>(out var on)
            ? on
            : throw new RpcFailedException(RpcFailure.NotTopAc, "The charger's start_charging has no value.");
    }

    /// <summary>Writes <c>current_limit</c>.</summary>
    public Task SetCurrentLimitAsync(int amps, CancellationToken cancellationToken = default) =>
        CallAsync(ComponentCall("Number.Set", "current_limit", amps.ToString(CultureInfo.InvariantCulture)), null, cancellationToken);

    /// <summary>Writes <c>start_charging</c>.</summary>
    public Task SetStartChargingAsync(bool on, CancellationToken cancellationToken = default) =>
        CallAsync(ComponentCall("Boolean.Set", "start_charging", on ? "true" : "false"), null, cancellationToken);

    /// <summary>The owner's settings, for the warning and the page.</summary>
    public async Task<OwnerSettings> GetOwnerSettingsAsync(CancellationToken cancellationToken = default)
    {
        var answer = await CallAsync("rpc/Service.GetConfig?id=0", null, cancellationToken).ConfigureAwait(false);
        return new OwnerSettings(Boolean(answer["auto_charge"]), Boolean(answer["auto_balance"]));
    }

    // One call. Every failure is an RpcFailedException that says which kind, so the plugin's status can say what
    // the owner can do about it.
    private async Task<JsonObject> CallAsync(string pathAndQuery, Uri? at, CancellationToken cancellationToken)
    {
        var address = at ?? _address.Current
            ?? throw new RpcFailedException(RpcFailure.NoAddress, "No address for the charger is set.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(new Uri(address, pathAndQuery), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcFailedException(RpcFailure.Unreachable, $"{address.Authority} did not answer within {_options.RequestTimeout.TotalSeconds:0.#} s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RpcFailedException(RpcFailure.Unreachable, $"{address.Authority} could not be reached: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new RpcFailedException(RpcFailure.Unauthorized,
                    "The charger asks for a password. Turn authentication off in the Shelly app: the plugin does not send one yet.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new RpcFailedException(RpcFailure.NotTopAc,
                    $"The charger answered HTTP {(int)response.StatusCode} to {pathAndQuery.Split('?')[0]}: is this a Shelly TopAC?");
            }

            try
            {
                return JsonNode.Parse(body) as JsonObject
                    ?? throw new RpcFailedException(RpcFailure.NotTopAc, $"The answer to {pathAndQuery.Split('?')[0]} is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new RpcFailedException(RpcFailure.NotTopAc, $"The answer to {pathAndQuery.Split('?')[0]} is not JSON.", ex);
            }
        }
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;

    private static bool? Boolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
