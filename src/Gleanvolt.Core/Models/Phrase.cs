using System.Text.Json.Serialization;

namespace Gleanvolt.Core.Models;

/// <summary>
/// Something a service says to a person, as a key and its arguments rather than as words (#290).
///
/// <para>The services are culture-invariant: the English sentence beside a phrase (a plan's
/// <c>Reason</c>, a problem's <c>Message</c>) is what the log, the API, MQTT and Home Assistant
/// carry, and it is never translated. A phrase is for the web UI, which looks <see cref="Key"/> up in
/// the viewer's language and formats each argument there — an energy in that language's numbers, a
/// time in the installation's zone. A key the UI does not know shows the English instead, so a
/// phrase can be added here before anyone has translated it.</para>
///
/// <para>Arguments are typed rather than pre-formatted for the same reason: "6.0kWh" written here is
/// English, and the point of the phrase is that the number is written by whoever reads it.</para>
/// </summary>
/// <param name="Key">Stable, and part of the contract with the UI's resources: renaming one is a
/// change to every translation of it.</param>
public sealed record Phrase(string Key, IReadOnlyList<PhraseArg> Args)
{
    /// <summary>A phrase with its arguments in order: <c>{0}</c>, <c>{1}</c>, … in the template.</summary>
    public static Phrase Of(string key, params PhraseArg[] args) => new(key, args);

    // By value, arguments included: a status that carries a phrase must still compare equal to the
    // same status a poll later, or everything that skips a re-render when nothing changed stops doing so.
    public bool Equals(Phrase? other) =>
        other is not null && Key == other.Key && Args.SequenceEqual(other.Args);

    public override int GetHashCode() =>
        Args.Aggregate(Key.GetHashCode(StringComparison.Ordinal), (hash, arg) => HashCode.Combine(hash, arg));
}

/// <summary>One argument of a <see cref="Phrase"/>, in its base unit; the UI decides how it reads.</summary>
/// <remarks>
/// Tagged in JSON, because a phrase is stored: a session's opening plan and its events keep theirs, and
/// an untagged abstract argument writes as <c>{}</c> and cannot be read back at all.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Energy), "energy")]
[JsonDerivedType(typeof(Power), "power")]
[JsonDerivedType(typeof(Percent), "percent")]
[JsonDerivedType(typeof(Clock), "clock")]
[JsonDerivedType(typeof(Date), "date")]
[JsonDerivedType(typeof(Duration), "duration")]
[JsonDerivedType(typeof(Number), "number")]
[JsonDerivedType(typeof(Text), "text")]
[JsonDerivedType(typeof(Said), "said")]
[JsonDerivedType(typeof(List), "list")]
[JsonDerivedType(typeof(Mode), "mode")]
public abstract record PhraseArg
{
    private PhraseArg()
    {
    }

    /// <summary>An energy, in Wh.</summary>
    public sealed record Energy(double WattHours) : PhraseArg;

    /// <summary>A power, in W.</summary>
    public sealed record Power(double Watts) : PhraseArg;

    /// <summary>A percentage, 0–100.</summary>
    public sealed record Percent(double Value) : PhraseArg;

    /// <summary>A moment, shown as a time of day in the installation's zone.</summary>
    public sealed record Clock(DateTimeOffset At) : PhraseArg;

    /// <summary>A moment, shown as a date in the installation's zone.</summary>
    public sealed record Date(DateTimeOffset At) : PhraseArg;

    /// <summary>A span of time.</summary>
    public sealed record Duration(TimeSpan Span) : PhraseArg;

    /// <summary>A plain count or code: an HTTP status, a register value, a row number.</summary>
    public sealed record Number(long Value) : PhraseArg;

    /// <summary>Words that are not ours to translate: a host name, an id, a third party's message.</summary>
    public sealed record Text(string Value) : PhraseArg;

    /// <summary>
    /// Something another service said, quoted inside this sentence: its phrase when it has one, and its
    /// English either way, which is what a reader without the phrase sees.
    /// </summary>
    public sealed record Said(Phrase? Phrase, string English) : PhraseArg;

    /// <summary>A charge mode, named the way the UI names it.</summary>
    public sealed record Mode(Enums.ChargeControlMode Value) : PhraseArg;

    /// <summary>Several arguments read as one list ("a VW ID, a password").</summary>
    public sealed record List(IReadOnlyList<PhraseArg> Items) : PhraseArg
    {
        public bool Equals(List? other) => other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Aggregate(0, HashCode.Combine);
    }
}
