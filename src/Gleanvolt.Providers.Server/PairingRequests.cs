using System.Net;
using System.Security.Cryptography;
using System.Text;
using Gleanvolt.Providers.Contracts;

namespace Gleanvolt.Providers.Server;

/// <summary>One plugin waiting for the owner's answer.</summary>
/// <param name="Id">Names the request; what the plugin collects with.</param>
/// <param name="Kind">The kind the plugin knocked for.</param>
/// <param name="Knock">What the plugin said about itself.</param>
/// <param name="Source">Where the knock came from.</param>
/// <param name="Code">The code the owner compares with the one the plugin shows.</param>
/// <param name="Since">When the plugin first knocked.</param>
/// <param name="LastSeen">When the plugin last knocked or collected; the entry expires from here.</param>
internal sealed record PairingEntry(
    string Id,
    ProviderKind Kind,
    PairingKnock Knock,
    IPAddress Source,
    string Code,
    DateTimeOffset Since,
    DateTimeOffset LastSeen)
{
    /// <summary>Whether the plugin runs on the machine Gleanvolt runs on.</summary>
    public bool OnThisMachine => IPAddress.IsLoopback(Source);
}

/// <summary>
/// The plugins that knocked and wait for the owner. Held in memory only: a restart makes the plugins knock again,
/// and nothing about a stranger's knock is worth keeping.
/// </summary>
public sealed class PairingRequests(TimeProvider time)
{
    /// <summary>The most entries kept; one more drops the oldest, so a flood cannot grow the list.</summary>
    internal const int MaxEntries = 16;

    /// <summary>How long an entry lives without the plugin knocking or collecting again.</summary>
    internal static readonly TimeSpan Expiry = TimeSpan.FromMinutes(15);

    /// <summary>The knocks one source may make in a minute; the next ones are refused.</summary>
    internal const int KnocksPerMinutePerSource = 10;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly List<Slot> _slots = [];
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _knocks = [];

    /// <summary>Raised when the waiting list changed, so a screen showing it can refresh.</summary>
    public event Action? Changed;

    /// <summary>The entries still waiting for the owner, oldest first. Expired ones are dropped here.</summary>
    internal IReadOnlyList<PairingEntry> Waiting
    {
        get
        {
            lock (_gate)
            {
                Expire();
                return _slots.Where(s => s.Decision is null).Select(s => s.Entry).ToList();
            }
        }
    }

    /// <summary>
    /// Lists a knock. The same plugin from the same address replaces its earlier entry (it restarted and has a new
    /// secret). Returns null when <paramref name="source"/> is over its rate limit.
    /// </summary>
    internal PairingEntry? Knock(ProviderKind kind, PairingKnock knock, IPAddress source)
    {
        PairingEntry entry;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!WithinRateLimit(source, now))
            {
                return null;
            }

            Expire();
            var replaced = _slots.FirstOrDefault(s => s.Decision is null
                && s.Entry.Kind.Id == kind.Id
                && s.Entry.Knock.ProviderId == knock.ProviderId
                && s.Entry.Source.Equals(source));
            var since = now;
            if (replaced is not null)
            {
                since = replaced.Entry.Since;
                _slots.Remove(replaced);
            }

            entry = new PairingEntry(Guid.NewGuid().ToString("N"), kind, knock, source,
                PairingSecret.Code(knock.SecretHash), since, now);
            _slots.Add(new Slot(entry));
            if (_slots.Count > MaxEntries)
            {
                _slots.RemoveAt(0);
            }
        }

        Changed?.Invoke();
        return entry;
    }

    /// <summary>
    /// What the plugin that proves it knows the secret is told. A wrong secret gets <see cref="PairingState.Unknown"/>
    /// and learns nothing about the entry. An answer is given once, then the entry is gone.
    /// </summary>
    internal PairingAnswer Collect(string requestId, string secret)
    {
        lock (_gate)
        {
            Expire();
            var slot = _slots.FirstOrDefault(s => s.Entry.Id == requestId);
            var hash = Encoding.UTF8.GetBytes(PairingSecret.Hash(secret));
            if (slot is null
                || !CryptographicOperations.FixedTimeEquals(hash, Encoding.UTF8.GetBytes(slot.Entry.Knock.SecretHash)))
            {
                return new PairingAnswer(PairingState.Unknown);
            }

            if (slot.Decision is { } decision)
            {
                _slots.Remove(slot);
                return decision;
            }

            slot.Entry = slot.Entry with { LastSeen = time.GetUtcNow() };
            return new PairingAnswer(PairingState.Waiting);
        }
    }

    /// <summary>
    /// Whether another waiting entry claims the same <see cref="PairingKnock.ProviderId"/> from a different source: two
    /// plugins cannot both be that one, so the owner is warned and neither can be accepted.
    /// </summary>
    internal bool IsDuplicate(PairingEntry entry)
    {
        lock (_gate)
        {
            Expire();
            return IsDuplicateLocked(entry);
        }
    }

    /// <summary>Accepts the entry: its plugin gets <paramref name="token"/> when it collects. False when unknown or a duplicate.</summary>
    internal bool Accept(string requestId, string token)
    {
        lock (_gate)
        {
            Expire();
            var slot = _slots.FirstOrDefault(s => s.Entry.Id == requestId && s.Decision is null);
            if (slot is null || IsDuplicateLocked(slot.Entry))
            {
                return false;
            }

            slot.Decision = new PairingAnswer(PairingState.Accepted, token);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Rejects the entry: its plugin is told so when it collects.</summary>
    internal void Reject(string requestId)
    {
        lock (_gate)
        {
            Expire();
            var slot = _slots.FirstOrDefault(s => s.Entry.Id == requestId && s.Decision is null);
            if (slot is null)
            {
                return;
            }

            slot.Decision = new PairingAnswer(PairingState.Rejected);
        }

        Changed?.Invoke();
    }

    private bool IsDuplicateLocked(PairingEntry entry) =>
        _slots.Any(s => s.Decision is null
            && s.Entry.Id != entry.Id
            && s.Entry.Knock.ProviderId == entry.Knock.ProviderId
            && !s.Entry.Source.Equals(entry.Source));

    private void Expire()
    {
        var limit = time.GetUtcNow() - Expiry;
        _slots.RemoveAll(s => s.Entry.LastSeen <= limit);
    }

    private bool WithinRateLimit(IPAddress source, DateTimeOffset now)
    {
        foreach (var stale in _knocks.Where(k => k.Value.Count == 0 || k.Value.Peek() <= now - Window).Select(k => k.Key).ToList())
        {
            var queue = _knocks[stale];
            while (queue.Count > 0 && queue.Peek() <= now - Window)
            {
                queue.Dequeue();
            }

            if (queue.Count == 0)
            {
                _knocks.Remove(stale);
            }
        }

        if (!_knocks.TryGetValue(source, out var times))
        {
            _knocks[source] = times = new Queue<DateTimeOffset>();
        }

        if (times.Count >= KnocksPerMinutePerSource)
        {
            return false;
        }

        times.Enqueue(now);
        return true;
    }

    private sealed class Slot(PairingEntry entry)
    {
        public PairingEntry Entry { get; set; } = entry;

        public PairingAnswer? Decision { get; set; }
    }
}
