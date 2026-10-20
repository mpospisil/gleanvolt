using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Gleanvolt.Discovery.Tests;

/// <summary>
/// A Modbus TCP server on loopback that answers the way the hardware does, without the hardware: units
/// with registers, a gateway that answers "nobody here" for the rest, or a device that accepts the
/// connection and never replies, as one whose connections are already taken does. It counts the
/// connections it has open, so a test can say the scan never held two at once.
/// </summary>
internal sealed class FakeModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly Task _accepting;
    private int _open;
    private int _maxOpen;

    public FakeModbusServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The units that answer, by unit id.</summary>
    public Dictionary<byte, FakeUnit> Units { get; } = [];

    /// <summary>What a unit id with no entry in <see cref="Units"/> gets: a gateway exception code, or silence (null).</summary>
    public byte? UnknownUnitException { get; set; }

    /// <summary>Accept every connection and never answer anything on it.</summary>
    public bool Silent { get; set; }

    public int Connections { get; private set; }

    public int OpenConnections => Volatile.Read(ref _open);

    public int MaxOpenConnections => Volatile.Read(ref _maxOpen);

    public int Writes { get; private set; }

    /// <summary>When each request arrived, in order.</summary>
    public ConcurrentQueue<(long Timestamp, byte UnitId, byte Function, ushort Address)> Requests { get; } = new();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping the listener is how the loop ends.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            _clients.Add(client);
            Connections++;
            var open = Interlocked.Increment(ref _open);
            InterlockedMax(ref _maxOpen, open);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var header = new byte[7];

            while (!_stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                var pdu = new byte[length - 1];
                await stream.ReadExactlyAsync(pdu, _stop.Token).ConfigureAwait(false);

                var unitId = header[6];
                var function = pdu[0];
                var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                Requests.Enqueue((Stopwatch.GetTimestamp(), unitId, function, address));

                if (Silent)
                {
                    continue;
                }

                byte[]? response;

                if (function is 0x06 or 0x10)
                {
                    Writes++;
                    response = Exception(function, 0x01);
                }
                else if (!Units.TryGetValue(unitId, out var unit))
                {
                    response = UnknownUnitException is { } code ? Exception(function, code) : null;
                }
                else
                {
                    var registers = function == 0x03 ? unit.Holding : unit.Input;
                    var values = Enumerable.Range(address, count)
                        .Select(offset => registers.TryGetValue((ushort)offset, out var value) ? value : (ushort?)null)
                        .ToList();

                    if (values.Any(value => value is null))
                    {
                        response = Exception(function, 0x02);
                    }
                    else
                    {
                        response = new byte[2 + count * 2];
                        response[0] = function;
                        response[1] = (byte)(count * 2);

                        for (var i = 0; i < count; i++)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2), values[i]!.Value);
                        }
                    }
                }

                if (response is null)
                {
                    continue;
                }

                var frame = new byte[7 + response.Length];
                header.AsSpan(0, 4).CopyTo(frame);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(response.Length + 1));
                frame[6] = unitId;
                response.CopyTo(frame, 7);
                await stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The client went away, or the server is stopping.
        }
        finally
        {
            Interlocked.Decrement(ref _open);
            client.Dispose();
        }
    }

    private static byte[] Exception(byte function, byte code) => [(byte)(function | 0x80), code];

    private static void InterlockedMax(ref int target, int value)
    {
        int current;

        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>One unit's registers.</summary>
internal sealed class FakeUnit
{
    public Dictionary<ushort, ushort> Holding { get; } = [];

    public Dictionary<ushort, ushort> Input { get; } = [];

    /// <summary>A SolaX inverter: its serial at holding 0x00, its battery SOC at input 0x1C.</summary>
    public static FakeUnit Inverter(string serial, ushort soc = 54) => new FakeUnit().WithSerial(0x0000, serial).WithInput(0x1C, soc);

    /// <summary>A SolaX charger: its serial at holding 0x600, its run mode at input 0x1D.</summary>
    public static FakeUnit Charger(string serial, ushort runMode = 2) => new FakeUnit().WithSerial(0x0600, serial).WithInput(0x1D, runMode);

    public FakeUnit WithSerial(ushort start, string serial)
    {
        var padded = serial.PadRight(14, '\0');

        for (var i = 0; i < 7; i++)
        {
            Holding[(ushort)(start + i)] = (ushort)((padded[i * 2] << 8) | padded[i * 2 + 1]);
        }

        return this;
    }

    public FakeUnit WithInput(ushort address, ushort value)
    {
        Input[address] = value;
        return this;
    }
}
