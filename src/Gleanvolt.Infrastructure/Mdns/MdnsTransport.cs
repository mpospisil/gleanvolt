using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Gleanvolt.Infrastructure.Mdns;

/// <summary>A network interface the responder can send on, with the addresses it answers for there.</summary>
public sealed record MdnsInterface(int Index, string Name, IReadOnlyList<IPAddress> Addresses);

/// <summary>A datagram as received: who sent it, and on which interface it arrived.</summary>
public sealed record MdnsDatagram(byte[] Data, IPEndPoint From, int InterfaceIndex);

/// <summary>
/// What the responder needs from the network, so the protocol can be tested on an in-memory LAN of
/// several responders and run for real over UDP 5353.
/// </summary>
public interface IMdnsTransport : IAsyncDisposable
{
    /// <summary>The interfaces to probe and announce on: up, multicast-capable, not loopback.</summary>
    IReadOnlyList<MdnsInterface> Interfaces();

    /// <summary>The addresses to answer with for a query that arrived on this interface.</summary>
    IReadOnlyList<IPAddress> AddressesOf(int interfaceIndex);

    ValueTask SendMulticastAsync(ReadOnlyMemory<byte> data, int interfaceIndex, CancellationToken cancellationToken);

    ValueTask SendUnicastAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken);

    ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Multicast DNS over IPv4 UDP 5353 (RFC 6762). The port is shared — <c>SO_REUSEADDR</c> — with
/// whatever else on the host speaks mDNS (avahi, Bonjour, Windows' own resolver), which is how several
/// responders on one machine coexist: each gets its own copy of every multicast datagram.
///
/// <para>One socket for every interface. The interface a query arrived on comes from the packet
/// information, so an answer carries only that interface's addresses: a phone on the Wi-Fi is never
/// handed a Docker bridge's 172.17.x address. A send picks its interface with <c>IP_MULTICAST_IF</c>,
/// under a lock, since that option belongs to the socket.</para>
/// </summary>
public sealed class UdpMdnsTransport : IMdnsTransport
{
    public const int MdnsPort = 5353;

    public static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");

    private static readonly TimeSpan InterfaceCacheLifetime = TimeSpan.FromSeconds(10);

    private readonly Socket _socket;
    private readonly bool _multicast;
    private readonly IPEndPoint _group;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<int> _joined = [];
    private readonly Lock _cacheLock = new();
    private readonly byte[] _buffer = new byte[9000];

    private IReadOnlyList<(MdnsInterface Interface, bool Loopback)> _cached = [];
    private DateTime _cachedAt = DateTime.MinValue;

    /// <param name="port">5353, except in a test, which binds a free port and sends unicast to it.</param>
    /// <param name="multicast">False in a test: no group is joined and multicast sends are dropped, so
    /// nothing reaches the LAN the test runs on.</param>
    public UdpMdnsTransport(int port = MdnsPort, bool multicast = true)
    {
        _multicast = multicast;
        _group = new IPEndPoint(MulticastGroup, MdnsPort);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _socket.ExclusiveAddressUse = false;
            }

            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));

            if (multicast)
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                JoinNewInterfaces();
            }
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    /// <summary>The port actually bound: 5353, or the free port a test asked for with 0.</summary>
    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public IReadOnlyList<MdnsInterface> Interfaces()
    {
        var interfaces = Snapshot().Where(entry => !entry.Loopback).Select(entry => entry.Interface).ToList();
        if (_multicast)
        {
            JoinNewInterfaces();
        }

        return interfaces;
    }

    public IReadOnlyList<IPAddress> AddressesOf(int interfaceIndex) =>
        Snapshot().FirstOrDefault(entry => entry.Interface.Index == interfaceIndex).Interface?.Addresses ?? [];

    public async ValueTask SendMulticastAsync(ReadOnlyMemory<byte> data, int interfaceIndex, CancellationToken cancellationToken)
    {
        if (!_multicast)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(interfaceIndex));
            await _socket.SendToAsync(data, SocketFlags.None, _group, cancellationToken);
        }
        catch (SocketException)
        {
            // An interface that went down between the snapshot and the send. The next announcement or
            // query on it is the retry; there is nothing to report.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask SendUnicastAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendToAsync(data, SocketFlags.None, destination, cancellationToken);
        }
        catch (SocketException)
        {
            // The asker is gone, or unreachable from here. Nothing else depends on this answer.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await _socket.ReceiveMessageFromAsync(_buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an earlier send's ICMP "port unreachable" on the next receive. It says
                // nothing about this socket; keep listening.
                continue;
            }

            return new MdnsDatagram(
                _buffer.AsSpan(0, result.ReceivedBytes).ToArray(),
                (IPEndPoint)result.RemoteEndPoint,
                result.PacketInformation.Interface);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    // Interfaces come and go — Wi-Fi joins after boot, DHCP hands out a new address — so the list is
    // re-read every few seconds rather than once. Reading it per datagram would be wasteful.
    private IReadOnlyList<(MdnsInterface Interface, bool Loopback)> Snapshot()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow - _cachedAt < InterfaceCacheLifetime)
            {
                return _cached;
            }

            var interfaces = new List<(MdnsInterface, bool)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                IPv4InterfaceProperties? ipv4;
                try
                {
                    properties = nic.GetIPProperties();
                    ipv4 = properties.GetIPv4Properties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                var loopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                if (ipv4 is null || (!loopback && !nic.SupportsMulticast))
                {
                    continue;
                }

                var addresses = properties.UnicastAddresses
                    .Select(unicast => unicast.Address)
                    .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .ToList();

                if (addresses.Any(address => address.AddressFamily == AddressFamily.InterNetwork))
                {
                    interfaces.Add((new MdnsInterface(ipv4.Index, nic.Name, addresses), loopback));
                }
            }

            _cached = interfaces;
            _cachedAt = DateTime.UtcNow;
            return interfaces;
        }
    }

    private void JoinNewInterfaces()
    {
        lock (_joined)
        {
            JoinNewInterfacesLocked();
        }
    }

    private void JoinNewInterfacesLocked()
    {
        foreach (var (nic, loopback) in Snapshot())
        {
            if (loopback || _joined.Contains(nic.Index))
            {
                continue;
            }

            try
            {
                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastGroup, nic.Index));
                _joined.Add(nic.Index);
            }
            catch (SocketException)
            {
                // Already a member, or an interface that refuses multicast after all. Tried again on the
                // next refresh; the others are unaffected.
            }
        }
    }
}
