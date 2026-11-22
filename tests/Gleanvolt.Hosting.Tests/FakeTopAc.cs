using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// A Shelly TopAC on the LAN (#429), as its Gen2 RPC API answers: the components under owner <c>service:0</c>, each
/// answering a GET with <c>{"value": …}</c>. Synthetic, written from Shelly's API documentation, until a tester's
/// snapshot replaces it; the three states the issue asks for (idle, a car waiting, charging) are
/// <see cref="Idle"/>, <see cref="Waiting"/> and <see cref="Charging"/>. Every write is recorded, in order.
/// </summary>
internal sealed class FakeTopAc : HttpMessageHandler
{
    private readonly object _gate = new();

    public string WorkState { get; set; } = "charger_free";

    /// <summary>As the charger sends it; in kW unless <see cref="TotalPowerInWatts"/>.</summary>
    public double TotalPowerKw { get; set; }

    public bool TotalPowerInWatts { get; set; }

    public double PhaseVoltage { get; set; } = 230;

    public double PhaseCurrent { get; set; }

    public int CurrentLimit { get; set; } = 16;

    public bool StartCharging { get; set; } = true;

    public bool AutoBalance { get; set; }

    /// <summary>Answer every call with this instead, when set: 401 for a charger with a password.</summary>
    public HttpStatusCode? Fail { get; set; }

    /// <summary>Every <c>.Set</c> call, as <c>role=value</c>.</summary>
    public List<string> Writes { get; } = [];

    public int Reads { get; private set; }

    public void Idle()
    {
        WorkState = "charger_free";
        PhaseCurrent = 0;
        TotalPowerKw = 0;
    }

    public void Waiting()
    {
        WorkState = "charger_wait";
        PhaseCurrent = 0;
        TotalPowerKw = 0;
    }

    /// <summary>Charging at <paramref name="amps"/> on three phases, total_power consistent with it.</summary>
    public void Charging(double amps)
    {
        WorkState = "charger_charging";
        PhaseCurrent = amps;
        TotalPowerKw = 3 * PhaseVoltage * amps / 1000;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Fail is { } status)
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var role = query["role"]?.Trim('"');
            var value = query["value"];

            JsonNode? answer = (path, role) switch
            {
                ("rpc/Enum.GetStatus", "work_state") => Value(WorkState),
                ("rpc/Object.GetStatus", "phase_info") => Value(PhaseInfo()),
                ("rpc/Number.GetStatus", "current_limit") => Value(CurrentLimit),
                ("rpc/Boolean.GetStatus", "start_charging") => Value(StartCharging),
                ("rpc/Service.GetConfig", _) => new JsonObject { ["auto_charge"] = true, ["auto_balance"] = AutoBalance },
                ("rpc/Number.Set", "current_limit") => Set(() => CurrentLimit = int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture)),
                ("rpc/Boolean.Set", "start_charging") => Set(() => StartCharging = bool.Parse(value!)),
                _ => null,
            };

            if (path.EndsWith("GetStatus", StringComparison.Ordinal))
            {
                Reads++;
            }

            if (path.EndsWith(".Set", StringComparison.Ordinal) && answer is not null)
            {
                Writes.Add($"{role}={value}");
            }

            return Task.FromResult(answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json"),
                });
        }
    }

    private JsonObject PhaseInfo()
    {
        var phase = new JsonObject
        {
            ["voltage"] = PhaseVoltage,
            ["current"] = PhaseCurrent,
            ["power"] = PhaseVoltage * PhaseCurrent,
        };

        return new JsonObject
        {
            ["total_power"] = TotalPowerInWatts ? TotalPowerKw * 1000 : TotalPowerKw,
            ["phase_a"] = phase.DeepClone(),
            ["phase_b"] = phase.DeepClone(),
            ["phase_c"] = phase.DeepClone(),
        };
    }

    private static JsonObject Value(JsonNode? value) => new() { ["value"] = value };

    private static JsonObject Value(string value) => new() { ["value"] = value };

    private static JsonObject Value(int value) => new() { ["value"] = value };

    private static JsonObject Value(bool value) => new() { ["value"] = value };

    private static JsonObject Set(Action apply)
    {
        apply();
        return new JsonObject { ["was_on"] = JsonValue.Create(true) };
    }
}
