using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Modbus;

namespace Gleanvolt.Chargers;

/// <summary>One exchange with the charger, as sent and as received.</summary>
/// <param name="At">When it was sent.</param>
/// <param name="Request">What was sent: a register read or write, or an HTTP request line.</param>
/// <param name="Response">What came back, or null when it failed.</param>
/// <param name="Error">Why it failed, or null.</param>
/// <param name="Milliseconds">How long it took.</param>
public sealed record RecordedExchange(DateTimeOffset At, string Request, string? Response, string? Error, long Milliseconds);

/// <summary>
/// The recording for testers (#350's register snapshot, as a plugin's own): every exchange with the charger for a
/// while, as sent and as received, while Gleanvolt runs as usual. A tester downloads it from the page and attaches
/// it to an issue; it is how a charger nobody here has is verified. Recorded by <see cref="RecordingModbusClient"/>
/// and <see cref="RecordingHandler"/>, so a plugin records whatever its transport sends. Never a password: the
/// HTTP recording leaves the Authorization header out.
/// </summary>
public sealed class DeviceRecorder
{
    /// <summary>How long a recording runs: long enough for a car to be plugged in and start.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    /// <summary>The most exchanges one recording keeps; the oldest go first.</summary>
    public const int MaxExchanges = 20_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Queue<RecordedExchange> _exchanges = new();
    private DateTimeOffset? _until;
    private DateTimeOffset? _started;

    public DeviceRecorder(TimeProvider time) => _time = time;

    /// <summary>Raised when a recording starts or stops.</summary>
    public event Action? Changed;

    /// <summary>Whether it is recording now.</summary>
    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _until is { } until && _time.GetUtcNow() < until;
            }
        }
    }

    /// <summary>Until when it records, while it does.</summary>
    public DateTimeOffset? Until
    {
        get
        {
            lock (_gate)
            {
                return Active ? _until : null;
            }
        }
    }

    /// <summary>How many exchanges the current or last recording holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _exchanges.Count;
            }
        }
    }

    /// <summary>Starts a new recording, dropping the last.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _exchanges.Clear();
            _started = _time.GetUtcNow();
            _until = _started + Duration;
        }

        Changed?.Invoke();
    }

    /// <summary>Stops recording; what was recorded is kept for the download.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _until = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Times <paramref name="exchange"/> and records it while a recording runs. The result or the exception passes through.</summary>
    public async Task<T> RecordAsync<T>(string request, Func<Task<T>> exchange, Func<T, string> describe)
    {
        if (!Active)
        {
            return await exchange().ConfigureAwait(false);
        }

        var at = _time.GetUtcNow();
        var started = _time.GetTimestamp();

        try
        {
            var result = await exchange().ConfigureAwait(false);
            Add(new RecordedExchange(at, request, describe(result), null, Elapsed(started)));
            return result;
        }
        catch (Exception ex)
        {
            Add(new RecordedExchange(at, request, null, $"{ex.GetType().Name}: {ex.Message}", Elapsed(started)));
            throw;
        }
    }

    /// <summary>The recording as a JSON document, with what the plugin is and when it ran.</summary>
    public string ToJson(string plugin, string version, object? device)
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(
                new
                {
                    plugin,
                    version,
                    started = _started,
                    device,
                    exchanges = _exchanges.ToArray(),
                },
                Json);
        }
    }

    private void Add(RecordedExchange exchange)
    {
        lock (_gate)
        {
            if (_exchanges.Count >= MaxExchanges)
            {
                _exchanges.Dequeue();
            }

            _exchanges.Enqueue(exchange);
        }
    }

    private long Elapsed(long started) => (long)_time.GetElapsedTime(started).TotalMilliseconds;

    internal static string Registers(ushort[] values) =>
        string.Join(' ', values.Select(value => value.ToString("X4", CultureInfo.InvariantCulture)));
}

/// <summary>A Modbus client that records every exchange while <see cref="DeviceRecorder"/> runs.</summary>
public sealed class RecordingModbusClient(IModbusClient inner, DeviceRecorder recorder) : IModbusClient
{
    public bool IsConnected => inner.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            $"read holding 0x{startAddress:X4} x{numberOfPoints}",
            () => inner.ReadHoldingRegistersAsync(startAddress, numberOfPoints, cancellationToken),
            DeviceRecorder.Registers);

    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort numberOfPoints, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            $"read input 0x{startAddress:X4} x{numberOfPoints}",
            () => inner.ReadInputRegistersAsync(startAddress, numberOfPoints, cancellationToken),
            DeviceRecorder.Registers);

    public Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            $"write 0x{address:X4} = {value:X4}",
            async () =>
            {
                await inner.WriteSingleRegisterAsync(address, value, cancellationToken).ConfigureAwait(false);
                return true;
            },
            _ => "ok");

    public Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default) =>
        recorder.RecordAsync(
            $"write 0x{startAddress:X4} = {DeviceRecorder.Registers(values)}",
            async () =>
            {
                await inner.WriteMultipleRegistersAsync(startAddress, values, cancellationToken).ConfigureAwait(false);
                return true;
            },
            _ => "ok");

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>An HTTP handler that records every request and its answer while <see cref="DeviceRecorder"/> runs.</summary>
public sealed class RecordingHandler(DeviceRecorder recorder) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!recorder.Active)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // The path and the query only: never the Authorization header.
        var line = $"{request.Method} {request.RequestUri?.PathAndQuery}";
        return await recorder.RecordAsync(
            line,
            async () =>
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                return response;
            },
            response => $"{(int)response.StatusCode} {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}").ConfigureAwait(false);
    }
}
