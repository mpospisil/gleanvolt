using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Gleanvolt.Chargers;
using Gleanvolt.Plugins;

namespace Gleanvolt.Providers.Testing;

/// <summary>
/// A Modbus TCP charger on a loopback port, for a Modbus charger plugin's tests (#443): a register map the test fills,
/// answering read holding (3), read input (4), write single (6) and write multiple (16). A register not in the map answers
/// "illegal data address", as a device does. Every write is kept, in order, for the test to check.
/// </summary>
public sealed class FakeModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public FakeModbusServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    /// <summary>The loopback port it listens on.</summary>
    public int Port { get; }

    /// <summary>Its address, as a plugin's settings take it.</summary>
    public DeviceAddress Address => new("127.0.0.1", Port);

    /// <summary>The holding registers, by address.</summary>
    public ConcurrentDictionary<ushort, ushort> Holding { get; } = new();

    /// <summary>The input registers, by address.</summary>
    public ConcurrentDictionary<ushort, ushort> Input { get; } = new();

    /// <summary>Every write, as <c>0xADDR=VALUE</c> (several values comma-separated), in order.</summary>
    public ConcurrentQueue<string> Writes { get; } = new();

    /// <summary>A 32-bit value in two holding registers, high word first.</summary>
    public void SetHolding32(ushort address, uint value)
    {
        Holding[address] = (ushort)(value >> 16);
        Holding[(ushort)(address + 1)] = (ushort)value;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();

        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];

            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                    var pdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(pdu, _stop.Token);

                    var answer = Answer(pdu);
                    var reply = new byte[7 + answer.Length];
                    header.AsSpan(0, 4).CopyTo(reply);
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), (ushort)(answer.Length + 1));
                    reply[6] = header[6];
                    answer.CopyTo(reply, 7);
                    await stream.WriteAsync(reply, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException or SocketException)
            {
            }
        }
    }

    private byte[] Answer(byte[] pdu)
    {
        var function = pdu[0];
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));

        switch (function)
        {
            case 3 or 4:
            {
                var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                var map = function == 3 ? Holding : Input;
                var answer = new byte[2 + (count * 2)];
                answer[0] = function;
                answer[1] = (byte)(count * 2);

                for (var i = 0; i < count; i++)
                {
                    if (!map.TryGetValue((ushort)(address + i), out var value))
                    {
                        return [(byte)(function | 0x80), 2];
                    }

                    BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2 + (i * 2)), value);
                }

                return answer;
            }

            case 6:
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                Holding[address] = value;
                Writes.Enqueue($"0x{address:X4}={value}");
                return pdu[..5];
            }

            case 16:
            {
                var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                var values = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + (i * 2)))).ToArray();
                for (var i = 0; i < count; i++)
                {
                    Holding[(ushort)(address + i)] = values[i];
                }

                Writes.Enqueue($"0x{address:X4}={string.Join(',', values)}");
                return pdu[..5];
            }

            default:
                return [(byte)(function | 0x80), 1];
        }
    }
}
