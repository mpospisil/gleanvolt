using System.Net;
using System.Net.Sockets;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// A minimal Modbus TCP server on loopback, just complete enough to answer the read/write function
/// codes this project uses — and, on demand, to answer them <em>badly</em>.
///
/// <para>Its reason for existing is <see cref="CorruptNextResponses"/>: replying with the wrong
/// transaction id reproduces the field failure from issue #24, where a stale response left in the
/// socket buffer put the stream permanently one reply behind. Nothing short of a real socket
/// reproduces it, because the bug lives in the interaction between NModbus's transaction validation
/// and the connection's lifetime.</para>
/// </summary>
public sealed class FakeModbusTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _connections;
    private int _responsesToCorrupt;
    private int _requestsHandled;

    public FakeModbusTcpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>How many times a client has (re)connected — the evidence that a reconnect happened.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>How many requests were answered, corrupt responses included.</summary>
    public int RequestsHandled => Volatile.Read(ref _requestsHandled);

    /// <summary>The value every register read returns.</summary>
    public ushort RegisterValue { get; set; } = 42;

    /// <summary>
    /// Answer the next <paramref name="count"/> requests with the wrong transaction id. Pass
    /// <see cref="Persistent"/> to keep doing it until <see cref="StopCorrupting"/>.
    ///
    /// <para>Note that NModbus retries a mismatched response by re-sending the request, so corrupting
    /// a single reply is <em>self-healing</em> and proves nothing. The failure this class exists to
    /// reproduce is the persistent one: once a stale reply is sitting in the buffer, every subsequent
    /// response is offset too, the retries are exhausted, and the read finally throws.</para>
    /// </summary>
    public void CorruptNextResponses(int count) => Volatile.Write(ref _responsesToCorrupt, count);

    /// <summary>Corrupt every response until told otherwise.</summary>
    public const int Persistent = int.MaxValue;

    /// <summary>Go back to answering correctly.</summary>
    public void StopCorrupting() => Volatile.Write(ref _responsesToCorrupt, 0);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (SocketException)
        {
            // Listener stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];

            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(stream, header, header.Length).ConfigureAwait(false))
                {
                    return;
                }

                // MBAP: transaction id, protocol id, length (unit + PDU), unit id.
                var transactionId = (ushort)((header[0] << 8) | header[1]);
                var pduLength = ((header[4] << 8) | header[5]) - 1;

                var pdu = new byte[pduLength];
                if (!await ReadExactlyAsync(stream, pdu, pduLength).ConfigureAwait(false))
                {
                    return;
                }

                var response = BuildResponse(header[6], pdu, transactionId);
                Interlocked.Increment(ref _requestsHandled);
                await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private byte[] BuildResponse(byte unitId, byte[] pdu, ushort transactionId)
    {
        // The whole point of the fake: hand back somebody else's transaction id, exactly as a stale
        // response left in the buffer would.
        var remaining = Volatile.Read(ref _responsesToCorrupt);
        if (remaining > 0)
        {
            if (remaining != Persistent)
            {
                Interlocked.Decrement(ref _responsesToCorrupt);
            }

            transactionId = (ushort)(transactionId == 0 ? ushort.MaxValue : transactionId - 1);
        }

        var function = pdu[0];
        byte[] responsePdu;

        switch (function)
        {
            case 3: // Read holding registers
            case 4: // Read input registers
                var count = (pdu[3] << 8) | pdu[4];
                responsePdu = new byte[2 + (count * 2)];
                responsePdu[0] = function;
                responsePdu[1] = (byte)(count * 2);
                for (var i = 0; i < count; i++)
                {
                    responsePdu[2 + (i * 2)] = (byte)(RegisterValue >> 8);
                    responsePdu[3 + (i * 2)] = (byte)(RegisterValue & 0xFF);
                }

                break;

            case 6:  // Write single register
            case 16: // Write multiple registers -- both echo address and value/quantity.
                responsePdu = [function, pdu[1], pdu[2], pdu[3], pdu[4]];
                break;

            default:
                responsePdu = [(byte)(function | 0x80), 1];
                break;
        }

        var length = responsePdu.Length + 1;
        var frame = new byte[6 + length];
        frame[0] = (byte)(transactionId >> 8);
        frame[1] = (byte)(transactionId & 0xFF);
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)(length & 0xFF);
        frame[6] = unitId;
        responsePdu.CopyTo(frame, 7);
        return frame;
    }

    private async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            int chunk;
            try
            {
                chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }

            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotent: tests stop the server early to simulate an unreachable device, and `await using`
        // then disposes it again at the end of the scope.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        _cts.Dispose();
    }
}
