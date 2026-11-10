using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Infrastructure.Mdns;

/// <summary>
/// What to advertise: one host name, and one service instance under each of several service types.
/// </summary>
/// <param name="HostLabel">The host name's single label, e.g. <c>gleanvolt</c> for <c>gleanvolt.local</c>.</param>
/// <param name="InstanceName">The service instance name a browser shows, e.g. <c>Gleanvolt</c>.</param>
/// <param name="Port">The TCP port every service is on.</param>
/// <param name="ServiceTypes">Service types without the domain, e.g. <c>_http._tcp</c>.</param>
/// <param name="Text">The TXT record's strings, <c>key=value</c>.</param>
public sealed record MdnsAdvertisement(
    string HostLabel,
    string InstanceName,
    ushort Port,
    IReadOnlyList<string> ServiceTypes,
    IReadOnlyList<string> Text);

/// <summary>RFC 6762's intervals, shortened by tests.</summary>
public sealed record MdnsTimings(TimeSpan ProbeInterval, TimeSpan AnnounceInterval, TimeSpan InterfaceRefresh)
{
    public static MdnsTimings Default { get; } = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}

/// <summary>
/// A multicast DNS responder for one host name and one service instance per service type (RFC 6762,
/// RFC 6763) — enough to be reachable as <c>gleanvolt.local</c> and listed by any DNS-SD browser.
///
/// <para><b>The name is claimed before it is used.</b> Three probes 250 ms apart ask whether anybody
/// already answers for the host name or the instance names. Any answer is a conflict, and the next
/// candidate is tried: <c>gleanvolt-2</c> and <c>Gleanvolt (2)</c>, and so on. A won name is announced
/// twice, a second apart, and defended: an answer for it from another device with different data
/// starts the probing again from the next candidate.</para>
///
/// <para><b>What this leaves out</b>, as unneeded for one appliance on a home LAN: simultaneous-probe
/// tie-breaking (§8.2 — two instances started in the same quarter second may both back off), the
/// random answer delays for shared records, truncation, and IPv6 transport. AAAA records are still
/// answered, over IPv4.</para>
/// </summary>
public sealed class MdnsResponder
{
    private const string LocalDomain = "local";
    private const string ServicesEnumeration = "_services._dns-sd._udp.local";
    private const uint HostTtl = 120;
    private const uint OtherTtl = 4500;
    private const uint LegacyTtlCap = 10;
    private const int MaxAttempts = 10;

    // How long a sent datagram is remembered, to recognise its echo. An echo is back within
    // milliseconds; the memory has to be short, because a second instance on the same machine has the
    // same addresses and so sends byte-identical probes — remembered for longer, the first instance
    // would take the newcomer's probe for its own echo and never answer it.
    private static readonly TimeSpan OwnDatagramMemory = TimeSpan.FromSeconds(1);

    private readonly IMdnsTransport _transport;
    private readonly MdnsAdvertisement _advertisement;
    private readonly MdnsTimings _timings;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _sent = new();
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<string> _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _attempt = 1;
    private Phase _phase = Phase.Probing;
    private bool _conflict;
    private TaskCompletionSource _reprobe = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MdnsResponder(IMdnsTransport transport, MdnsAdvertisement advertisement, ILogger? logger = null, MdnsTimings? timings = null)
    {
        _transport = transport;
        _advertisement = advertisement;
        _logger = logger ?? NullLogger.Instance;
        _timings = timings ?? MdnsTimings.Default;
    }

    private enum Phase
    {
        Probing,
        Claimed,
        GaveUp,
    }

    /// <summary>The host name won, e.g. <c>gleanvolt-2.local</c>; null until one is.</summary>
    public string? HostName { get; private set; }

    /// <summary>The instance name won, e.g. <c>Gleanvolt (2)</c>; null until one is.</summary>
    public string? InstanceName { get; private set; }

    /// <summary>Completes with the first host name won.</summary>
    public Task<string> FirstClaim => _firstClaim.Task;

    /// <summary>Called with the host and instance names every time a name is won, the first time included.</summary>
    public Action<string, string>? Claimed { get; init; }

    /// <summary>Claims a name, answers for it until cancelled, then says goodbye.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiving = ReceiveLoopAsync(stop.Token);

        try
        {
            await ClaimAsync(1, stop.Token);

            while (!stop.IsCancellationRequested)
            {
                Task reprobe;
                lock (_lock)
                {
                    reprobe = _reprobe.Task;
                }

                var refresh = Task.Delay(_timings.InterfaceRefresh, stop.Token);
                if (await Task.WhenAny(reprobe, refresh) == reprobe)
                {
                    int next;
                    lock (_lock)
                    {
                        _reprobe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        next = _attempt + 1;
                    }

                    _logger.LogWarning("Another device on the LAN now answers for {HostName}; claiming a new name.", HostName);
                    await ClaimAsync(next, stop.Token);
                }
                else
                {
                    await refresh;

                    // Joins any interface that came up since, and tells it about us.
                    if (CurrentPhase() == Phase.Claimed)
                    {
                        await AnnounceAsync(ttlFactor: 1, stop.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            if (CurrentPhase() == Phase.Claimed)
            {
                using var goodbye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await AnnounceAsync(ttlFactor: 0, goodbye.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException)
                {
                    // Best effort: caches expire the records by themselves within two minutes.
                }
            }

            await stop.CancelAsync();
            try
            {
                await receiving;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private Phase CurrentPhase()
    {
        lock (_lock)
        {
            return _phase;
        }
    }

    private async Task ClaimAsync(int firstAttempt, CancellationToken cancellationToken)
    {
        for (var attempt = firstAttempt; attempt <= MaxAttempts; attempt++)
        {
            lock (_lock)
            {
                _attempt = attempt;
                _phase = Phase.Probing;
                _conflict = false;
            }

            for (var probe = 0; probe < 3; probe++)
            {
                await SendProbeAsync(cancellationToken);
                await Task.Delay(_timings.ProbeInterval, cancellationToken);
            }

            var host = HostFqdn(attempt);
            var instance = Instance(attempt);
            lock (_lock)
            {
                if (_conflict)
                {
                    _logger.LogInformation("{HostName} is taken on the LAN; trying the next name.", host);
                    continue;
                }

                _phase = Phase.Claimed;
                HostName = host;
                InstanceName = instance;
            }

            await AnnounceAsync(ttlFactor: 1, cancellationToken);
            Claimed?.Invoke(host, instance);
            _firstClaim.TrySetResult(host);

            await Task.Delay(_timings.AnnounceInterval, cancellationToken);
            await AnnounceAsync(ttlFactor: 1, cancellationToken);
            return;
        }

        lock (_lock)
        {
            _phase = Phase.GaveUp;
        }

        _logger.LogWarning(
            "Gave up advertising on the LAN: {First} to {Last} are all taken.", HostFqdn(1), HostFqdn(MaxAttempts));
        _firstClaim.TrySetException(new InvalidOperationException("No mDNS name could be claimed."));
    }

    private string HostLabel(int attempt) => attempt == 1 ? _advertisement.HostLabel : $"{_advertisement.HostLabel}-{attempt}";

    private string HostFqdn(int attempt) => $"{HostLabel(attempt)}.{LocalDomain}";

    private string Instance(int attempt) => attempt == 1 ? _advertisement.InstanceName : $"{_advertisement.InstanceName} ({attempt})";

    private static string ServiceFqdn(string serviceType) => $"{serviceType}.{LocalDomain}";

    private static string InstanceFqdn(string instance, string serviceType) => $"{instance}.{serviceType}.{LocalDomain}";

    private async Task SendProbeAsync(CancellationToken cancellationToken)
    {
        int attempt;
        lock (_lock)
        {
            attempt = _attempt;
        }

        var questions = new List<DnsQuestion> { new(HostFqdn(attempt), DnsType.Any, UnicastResponse: true) };
        questions.AddRange(_advertisement.ServiceTypes.Select(type => new DnsQuestion(InstanceFqdn(Instance(attempt), type), DnsType.Any, UnicastResponse: true)));

        foreach (var nic in _transport.Interfaces())
        {
            // The proposed records go in the authority section (§8.2), so a responder that does break
            // ties can see what this one wants.
            var probe = DnsMessage.Query([.. questions]);
            probe.Authorities.AddRange(Records(attempt, nic.Addresses, ttlFactor: 1, legacy: false));
            await SendAsync(probe, nic.Index, destination: null, cancellationToken);
        }
    }

    private async Task AnnounceAsync(int ttlFactor, CancellationToken cancellationToken)
    {
        int attempt;
        lock (_lock)
        {
            attempt = _attempt;
        }

        foreach (var nic in _transport.Interfaces())
        {
            var announcement = DnsMessage.Response();
            announcement.Answers.AddRange(Records(attempt, nic.Addresses, ttlFactor, legacy: false));
            announcement.Answers.AddRange(_advertisement.ServiceTypes.Select(type =>
                DnsRecord.Pointer(ServicesEnumeration, ServiceFqdn(type), OtherTtl * (uint)ttlFactor)));
            await SendAsync(announcement, nic.Index, destination: null, cancellationToken);
        }
    }

    // Every record this responder owns, for one interface's addresses.
    private List<DnsRecord> Records(int attempt, IReadOnlyList<IPAddress> addresses, int ttlFactor, bool legacy)
    {
        var host = HostFqdn(attempt);
        var instance = Instance(attempt);
        var records = new List<DnsRecord>();
        records.AddRange(AddressRecords(host, addresses, ttlFactor, legacy));

        foreach (var type in _advertisement.ServiceTypes)
        {
            var instanceName = InstanceFqdn(instance, type);
            records.Add(Ttl(DnsRecord.Pointer(ServiceFqdn(type), instanceName, OtherTtl), ttlFactor, legacy));
            records.Add(Ttl(DnsRecord.Service(instanceName, _advertisement.Port, host, HostTtl), ttlFactor, legacy));
            records.Add(Ttl(DnsRecord.Text(instanceName, _advertisement.Text, OtherTtl), ttlFactor, legacy));
        }

        return records;
    }

    private static IEnumerable<DnsRecord> AddressRecords(string host, IReadOnlyList<IPAddress> addresses, int ttlFactor, bool legacy) =>
        addresses.Select(address => Ttl(DnsRecord.Address(host, address, HostTtl), ttlFactor, legacy));

    // A goodbye is the same records with TTL 0 (§10.1). A legacy unicast answer caps the TTL at 10 s
    // and drops the cache-flush bit (§6.7), since the asker is a plain resolver, not an mDNS cache.
    private static DnsRecord Ttl(DnsRecord record, int ttlFactor, bool legacy) => legacy
        ? record with { Ttl = Math.Min(record.Ttl, LegacyTtlCap), CacheFlush = false }
        : record with { Ttl = record.Ttl * (uint)ttlFactor };

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MdnsDatagram datagram;
            try
            {
                datagram = await _transport.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogDebug(exception, "mDNS receive failed; listening again.");
                continue;
            }

            try
            {
                await HandleAsync(datagram, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is SocketException or ArgumentException)
            {
                _logger.LogDebug(exception, "mDNS datagram from {From} could not be answered.", datagram.From);
            }
        }
    }

    private async Task HandleAsync(MdnsDatagram datagram, CancellationToken cancellationToken)
    {
        // Our own multicast comes back to us (loopback is on, so that avahi on this very machine hears
        // us too). Comparing addresses cannot tell it from a second instance on the same host, which
        // has the same address and port, so the bytes are compared instead.
        if (_sent.TryGetValue(Fingerprint(datagram.Data), out var sentAt) && DateTime.UtcNow - sentAt <= OwnDatagramMemory)
        {
            return;
        }

        var message = DnsMessage.TryParse(datagram.Data);
        if (message is null)
        {
            return;
        }

        if (message.IsResponse)
        {
            CheckForConflict(message);
            return;
        }

        int attempt;
        lock (_lock)
        {
            if (_phase != Phase.Claimed)
            {
                return;
            }

            attempt = _attempt;
        }

        var addresses = _transport.AddressesOf(datagram.InterfaceIndex);
        if (addresses.Count == 0)
        {
            addresses = [.. _transport.Interfaces().SelectMany(nic => nic.Addresses)];
        }

        var legacy = datagram.From.Port != UdpMdnsTransport.MdnsPort;
        var reply = Answer(message, attempt, addresses, legacy);
        if (reply is null)
        {
            return;
        }

        // A legacy resolver is answered directly: it is not listening on 5353. Everything else, a probe's
        // QU questions included, is answered by multicast (§5.4 allows it). A unicast answer to port
        // 5353 reaches only one of the sockets sharing that port on the asker's machine — often avahi's,
        // not the asker's — so a second instance on the same host would never see that the name is taken.
        if (legacy)
        {
            await SendAsync(reply, datagram.InterfaceIndex, datagram.From, cancellationToken);
        }
        else
        {
            await SendAsync(reply, datagram.InterfaceIndex, destination: null, cancellationToken);
        }
    }

    private void CheckForConflict(DnsMessage response)
    {
        lock (_lock)
        {
            if (_phase == Phase.GaveUp)
            {
                return;
            }

            var host = HostFqdn(_attempt);
            var instances = _advertisement.ServiceTypes.Select(type => InstanceFqdn(Instance(_attempt), type)).ToList();
            var ours = _phase == Phase.Claimed
                ? _transport.Interfaces().SelectMany(nic => nic.Addresses).ToHashSet()
                : [];

            foreach (var record in response.Answers.Concat(response.Additionals))
            {
                if (record.Ttl == 0)
                {
                    continue; // A goodbye: somebody letting go of a name, not holding it.
                }

                var forHost = DnsMessage.SameName(record.Name, host) && record.Type is DnsType.A or DnsType.Aaaa;
                var forInstance = instances.Any(instance => DnsMessage.SameName(record.Name, instance))
                    && record.Type is DnsType.Srv or DnsType.Txt;
                if (!forHost && !forInstance)
                {
                    continue;
                }

                if (_phase == Phase.Probing)
                {
                    // While probing, any answer at all for a name we want means somebody has it.
                    _conflict = true;
                    return;
                }

                // Once claimed, our own data echoed back by a cache is not a conflict; different data is.
                var different = record.Data switch
                {
                    AddressRData address => !ours.Contains(address.Address),
                    SrvRData service => !DnsMessage.SameName(service.Target, host) || service.Port != _advertisement.Port,
                    _ => false,
                };

                if (different)
                {
                    _reprobe.TrySetResult();
                    return;
                }
            }
        }
    }

    private DnsMessage? Answer(DnsMessage query, int attempt, IReadOnlyList<IPAddress> addresses, bool legacy)
    {
        var host = HostFqdn(attempt);
        var instance = Instance(attempt);
        var answers = new List<DnsRecord>();
        var additionals = new List<DnsRecord>();

        void Add(List<DnsRecord> list, DnsRecord record)
        {
            if (!list.Contains(record))
            {
                list.Add(record);
            }
        }

        foreach (var question in query.Questions)
        {
            var any = question.Type == DnsType.Any;

            if (DnsMessage.SameName(question.Name, host))
            {
                foreach (var record in AddressRecords(host, addresses, 1, legacy))
                {
                    if (any || record.Type == question.Type)
                    {
                        Add(answers, record);
                    }
                }
            }

            if (DnsMessage.SameName(question.Name, ServicesEnumeration) && (any || question.Type == DnsType.Ptr))
            {
                foreach (var type in _advertisement.ServiceTypes)
                {
                    Add(answers, Ttl(DnsRecord.Pointer(ServicesEnumeration, ServiceFqdn(type), OtherTtl), 1, legacy));
                }
            }

            foreach (var type in _advertisement.ServiceTypes)
            {
                var instanceName = InstanceFqdn(instance, type);
                var srv = Ttl(DnsRecord.Service(instanceName, _advertisement.Port, host, HostTtl), 1, legacy);
                var txt = Ttl(DnsRecord.Text(instanceName, _advertisement.Text, OtherTtl), 1, legacy);

                if (DnsMessage.SameName(question.Name, ServiceFqdn(type)) && (any || question.Type == DnsType.Ptr))
                {
                    Add(answers, Ttl(DnsRecord.Pointer(ServiceFqdn(type), instanceName, OtherTtl), 1, legacy));
                    Add(additionals, srv);
                    Add(additionals, txt);
                    foreach (var record in AddressRecords(host, addresses, 1, legacy))
                    {
                        Add(additionals, record);
                    }
                }

                if (DnsMessage.SameName(question.Name, instanceName))
                {
                    if (any || question.Type == DnsType.Srv)
                    {
                        Add(answers, srv);
                        foreach (var record in AddressRecords(host, addresses, 1, legacy))
                        {
                            Add(additionals, record);
                        }
                    }

                    if (any || question.Type == DnsType.Txt)
                    {
                        Add(answers, txt);
                    }
                }
            }
        }

        // Known-answer suppression (§7.1): the asker already holds these, with at least half their life left.
        answers.RemoveAll(answer => query.Answers.Any(known =>
            DnsMessage.SameName(known.Name, answer.Name)
            && known.Type == answer.Type
            && known.Data == answer.Data
            && known.Ttl >= answer.Ttl / 2));

        if (answers.Count == 0)
        {
            return null;
        }

        additionals.RemoveAll(record => answers.Contains(record));

        var reply = DnsMessage.Response(legacy ? query.Id : (ushort)0);
        if (legacy)
        {
            // A legacy resolver matches the answer to its question (§6.7).
            reply.Questions.AddRange(query.Questions);
        }

        reply.Answers.AddRange(answers);
        reply.Additionals.AddRange(additionals);
        return reply;
    }

    private async Task SendAsync(DnsMessage message, int interfaceIndex, IPEndPoint? destination, CancellationToken cancellationToken)
    {
        var bytes = message.Encode();
        Remember(bytes);

        if (destination is null)
        {
            await _transport.SendMulticastAsync(bytes, interfaceIndex, cancellationToken);
        }
        else
        {
            await _transport.SendUnicastAsync(bytes, destination, cancellationToken);
        }
    }

    private void Remember(byte[] bytes)
    {
        var now = DateTime.UtcNow;
        _sent[Fingerprint(bytes)] = now;

        foreach (var (key, at) in _sent)
        {
            if (now - at > OwnDatagramMemory)
            {
                _sent.TryRemove(key, out _);
            }
        }
    }

    private static string Fingerprint(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));
}
