using System.Globalization;

namespace Gleanvolt.Chargers;

/// <summary>
/// One text in the two languages Gleanvolt speaks. The page shows the viewer's (<see cref="ToString"/>, from the
/// culture Gleanvolt opened it with), and a status sends both (<see cref="All"/>), since one status goes to every
/// viewer of Gleanvolt.
/// </summary>
/// <param name="En">English.</param>
/// <param name="Cs">Czech.</param>
public sealed record Words(string En, string Cs)
{
    /// <summary>Whether the current request or circuit is in Czech.</summary>
    public static bool Czech => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "cs";

    /// <summary>Both, keyed by language tag, for <c>ProviderStatus.Messages</c>.</summary>
    public IReadOnlyDictionary<string, string> All => new Dictionary<string, string> { ["en"] = En, ["cs"] = Cs };

    /// <summary>This, then <paramref name="more"/>, as one text in each language.</summary>
    public Words Then(Words more) => new($"{En} {more.En}", $"{Cs} {more.Cs}");

    /// <summary>Fills <c>{0}</c>, <c>{1}</c>, … in both.</summary>
    public Words Format(params object?[] args) =>
        new(string.Format(CultureInfo.InvariantCulture, En, args), string.Format(CultureInfo.InvariantCulture, Cs, args));

    /// <summary>The viewer's language.</summary>
    public override string ToString() => Czech ? Cs : En;
}
