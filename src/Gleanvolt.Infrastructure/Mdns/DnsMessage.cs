using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gleanvolt.Infrastructure.Mdns;

/// <summary>The record types the responder reads or writes (RFC 1035, 2782, 3596, 6762).</summary>
public static class DnsType
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Any = 255;
}

/// <summary>One question. <see cref="UnicastResponse"/> is mDNS's "QU" bit, the top bit of the class.</summary>
public sealed record DnsQuestion(string Name, ushort Type, bool UnicastResponse = false);

/// <summary>A record's data, decoded for the types this responder needs and kept raw for the rest.</summary>
public abstract record DnsRData;

public sealed record AddressRData(IPAddress Address) : DnsRData;

public sealed record PtrRData(string Target) : DnsRData;

public sealed record SrvRData(ushort Priority, ushort Weight, ushort Port, string Target) : DnsRData;

public sealed record TxtRData(IReadOnlyList<string> Strings) : DnsRData
{
    public bool Equals(TxtRData? other) => other is not null && Strings.SequenceEqual(other.Strings);

    public override int GetHashCode() => Strings.Aggregate(0, (hash, s) => HashCode.Combine(hash, s));
}

public sealed record RawRData(byte[] Bytes) : DnsRData
{
    public bool Equals(RawRData? other) => other is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);

    public override int GetHashCode() => Bytes.Length;
}

/// <summary>
/// One resource record. <see cref="CacheFlush"/> is mDNS's top bit of the class (RFC 6762 §10.2): set on
/// records that only this host may answer, so a cache replaces rather than adds to what it holds.
/// </summary>
public sealed record DnsRecord(string Name, ushort Type, uint Ttl, DnsRData Data, bool CacheFlush = false)
{
    public static DnsRecord Address(string name, IPAddress address, uint ttl) =>
        new(name, address.AddressFamily == AddressFamily.InterNetworkV6 ? DnsType.Aaaa : DnsType.A, ttl, new AddressRData(address), CacheFlush: true);

    public static DnsRecord Pointer(string name, string target, uint ttl) =>
        new(name, DnsType.Ptr, ttl, new PtrRData(target));

    public static DnsRecord Service(string name, ushort port, string target, uint ttl) =>
        new(name, DnsType.Srv, ttl, new SrvRData(0, 0, port, target), CacheFlush: true);

    public static DnsRecord Text(string name, IReadOnlyList<string> strings, uint ttl) =>
        new(name, DnsType.Txt, ttl, new TxtRData(strings), CacheFlush: true);
}

/// <summary>
/// A DNS message, as much of RFC 1035 as multicast DNS uses: header, questions and the three record
/// sections. Names are written uncompressed (every message here is a few hundred bytes) and read with
/// compression, since other responders compress.
/// </summary>
public sealed class DnsMessage
{
    private const ushort ResponseFlag = 0x8000;
    private const ushort AuthoritativeFlag = 0x0400;
    private const ushort ClassIn = 1;
    private const ushort TopBit = 0x8000;

    public ushort Id { get; init; }

    public ushort Flags { get; init; }

    public bool IsResponse => (Flags & ResponseFlag) != 0;

    public List<DnsQuestion> Questions { get; init; } = [];

    public List<DnsRecord> Answers { get; init; } = [];

    public List<DnsRecord> Authorities { get; init; } = [];

    public List<DnsRecord> Additionals { get; init; } = [];

    /// <summary>An mDNS query (RFC 6762 §18): id 0, no flags.</summary>
    public static DnsMessage Query(params DnsQuestion[] questions) => new() { Questions = [.. questions] };

    /// <summary>An mDNS response: authoritative, id 0 unless it answers a legacy unicast query.</summary>
    public static DnsMessage Response(ushort id = 0) => new() { Id = id, Flags = ResponseFlag | AuthoritativeFlag };

    public byte[] Encode()
    {
        var buffer = new List<byte>(512);
        WriteUInt16(buffer, Id);
        WriteUInt16(buffer, Flags);
        WriteUInt16(buffer, (ushort)Questions.Count);
        WriteUInt16(buffer, (ushort)Answers.Count);
        WriteUInt16(buffer, (ushort)Authorities.Count);
        WriteUInt16(buffer, (ushort)Additionals.Count);

        foreach (var question in Questions)
        {
            WriteName(buffer, question.Name);
            WriteUInt16(buffer, question.Type);
            WriteUInt16(buffer, (ushort)(ClassIn | (question.UnicastResponse ? TopBit : 0)));
        }

        foreach (var record in Answers.Concat(Authorities).Concat(Additionals))
        {
            WriteRecord(buffer, record);
        }

        return [.. buffer];
    }

    /// <summary>Reads a message, or returns null for anything malformed: a LAN is not a trusted input.</summary>
    public static DnsMessage? TryParse(ReadOnlySpan<byte> data)
    {
        try
        {
            return Parse(data.ToArray());
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or FormatException or InvalidDataException)
        {
            return null;
        }
    }

    private static DnsMessage Parse(byte[] data)
    {
        if (data.Length < 12)
        {
            throw new InvalidDataException("Shorter than a DNS header.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0));
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        var counts = new int[4];
        for (var i = 0; i < 4; i++)
        {
            counts[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4 + (2 * i)));
        }

        var offset = 12;
        var questions = new List<DnsQuestion>(counts[0]);
        for (var i = 0; i < counts[0]; i++)
        {
            var name = ReadName(data, ref offset);
            var type = ReadUInt16(data, ref offset);
            var klass = ReadUInt16(data, ref offset);
            questions.Add(new DnsQuestion(name, type, (klass & TopBit) != 0));
        }

        var sections = new List<DnsRecord>[3];
        for (var s = 0; s < 3; s++)
        {
            sections[s] = new List<DnsRecord>(counts[s + 1]);
            for (var i = 0; i < counts[s + 1]; i++)
            {
                sections[s].Add(ReadRecord(data, ref offset));
            }
        }

        return new DnsMessage
        {
            Id = id,
            Flags = flags,
            Questions = questions,
            Answers = sections[0],
            Authorities = sections[1],
            Additionals = sections[2],
        };
    }

    /// <summary>Names compare case-insensitively (RFC 1035 §2.3.3) and without a trailing dot.</summary>
    public static bool SameName(string left, string right) =>
        string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static void WriteRecord(List<byte> buffer, DnsRecord record)
    {
        WriteName(buffer, record.Name);
        WriteUInt16(buffer, record.Type);
        WriteUInt16(buffer, (ushort)(ClassIn | (record.CacheFlush ? TopBit : 0)));
        WriteUInt32(buffer, record.Ttl);

        var data = new List<byte>(32);
        switch (record.Data)
        {
            case AddressRData address:
                data.AddRange(address.Address.GetAddressBytes());
                break;
            case PtrRData pointer:
                WriteName(data, pointer.Target);
                break;
            case SrvRData service:
                WriteUInt16(data, service.Priority);
                WriteUInt16(data, service.Weight);
                WriteUInt16(data, service.Port);
                WriteName(data, service.Target);
                break;
            case TxtRData text:
                foreach (var entry in text.Strings)
                {
                    var bytes = Encoding.UTF8.GetBytes(entry);
                    if (bytes.Length > 255)
                    {
                        throw new ArgumentException($"A TXT string is at most 255 bytes: '{entry}'.");
                    }

                    data.Add((byte)bytes.Length);
                    data.AddRange(bytes);
                }

                if (text.Strings.Count == 0)
                {
                    data.Add(0); // RFC 6763 §6.1: an empty TXT record is a single zero byte.
                }

                break;
            case RawRData raw:
                data.AddRange(raw.Bytes);
                break;
        }

        WriteUInt16(buffer, (ushort)data.Count);
        buffer.AddRange(data);
    }

    private static DnsRecord ReadRecord(byte[] data, ref int offset)
    {
        var name = ReadName(data, ref offset);
        var type = ReadUInt16(data, ref offset);
        var klass = ReadUInt16(data, ref offset);
        var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        var length = ReadUInt16(data, ref offset);
        var end = offset + length;
        if (end > data.Length)
        {
            throw new InvalidDataException("Record data runs past the message.");
        }

        var start = offset;
        DnsRData rdata = type switch
        {
            DnsType.A when length == 4 => new AddressRData(new IPAddress(data.AsSpan(start, 4))),
            DnsType.Aaaa when length == 16 => new AddressRData(new IPAddress(data.AsSpan(start, 16))),
            DnsType.Ptr => new PtrRData(ReadName(data, ref start)),
            DnsType.Srv => ReadService(data, start),
            DnsType.Txt => ReadText(data, start, end),
            _ => new RawRData(data.AsSpan(start, length).ToArray()),
        };

        offset = end;
        return new DnsRecord(name, type, ttl, rdata, (klass & TopBit) != 0);
    }

    private static SrvRData ReadService(byte[] data, int offset)
    {
        var priority = ReadUInt16(data, ref offset);
        var weight = ReadUInt16(data, ref offset);
        var port = ReadUInt16(data, ref offset);
        return new SrvRData(priority, weight, port, ReadName(data, ref offset));
    }

    private static TxtRData ReadText(byte[] data, int offset, int end)
    {
        var strings = new List<string>();
        while (offset < end)
        {
            var length = data[offset++];
            if (offset + length > end)
            {
                throw new InvalidDataException("A TXT string runs past its record.");
            }

            if (length > 0)
            {
                strings.Add(Encoding.UTF8.GetString(data, offset, length));
            }

            offset += length;
        }

        return new TxtRData(strings);
    }

    private static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;

        while (true)
        {
            var length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                // A compression pointer. Bounded, so a looping pointer is malformed input, not a hang.
                if (++jumps > 32)
                {
                    throw new InvalidDataException("Too many compression pointers.");
                }

                var target = ((length & 0x3F) << 8) | data[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                    jumped = true;
                }

                position = target;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw new InvalidDataException("Unsupported label type.");
            }

            labels.Add(Encoding.UTF8.GetString(data, position + 1, length));
            position += 1 + length;
        }

        if (!jumped)
        {
            offset = position;
        }

        return string.Join('.', labels);
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"A DNS label is at most 63 bytes: '{label}'.");
            }

            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0);
    }

    private static ushort ReadUInt16(byte[] data, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    private static void WriteUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        WriteUInt16(buffer, (ushort)(value >> 16));
        WriteUInt16(buffer, (ushort)value);
    }
}
