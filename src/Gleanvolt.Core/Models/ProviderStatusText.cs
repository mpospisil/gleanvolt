namespace Gleanvolt.Core.Models;

/// <summary>
/// An external provider's status, worded for every language (#415). The provider sends a code from a
/// fixed list, which Gleanvolt words itself (<c>ServiceText</c>, <c>ProviderStatus_*</c>), and its own
/// detail, in as many languages as it has. One status goes to every viewer, so it can't be translated by
/// the provider for the one reading it.
/// </summary>
public static class ProviderStatusText
{
    /// <summary>
    /// The phrase and its English for <paramref name="code"/> and the detail. The codes are the provider
    /// contract's <c>ProviderStatusCodes</c>. With a code Gleanvolt knows, the detail is
    /// <paramref name="messages"/> alone: <paramref name="message"/> is then the provider's whole sentence
    /// for an older Gleanvolt and would say the code again. Without one, the detail is
    /// <paramref name="messages"/> or <paramref name="message"/>. Null when there is nothing to say.
    /// </summary>
    public static (Phrase? Phrase, string English)? Of(string? code, IReadOnlyDictionary<string, string>? messages, string? message)
    {
        var coded = Coded(code);
        var english = messages?.FirstOrDefault(text => string.Equals(text.Key, "en", StringComparison.OrdinalIgnoreCase)).Value;

        if (coded is null && string.IsNullOrEmpty(english))
        {
            english = message;
        }

        PhraseArg? detail = messages is { Count: > 0 }
            ? new PhraseArg.Translations(messages, english ?? message ?? string.Empty)
            : coded is null && english is { Length: > 0 } ? new PhraseArg.Text(english) : null;

        if (coded is not { } said)
        {
            return detail is null ? null : (Phrase.Of("ProviderStatus.Detail", detail), english ?? message ?? string.Empty);
        }

        if (detail is null)
        {
            return said;
        }

        return (Phrase.Of("ProviderStatus.WithDetail", new PhraseArg.Said(said.Phrase, said.English), detail),
            string.IsNullOrEmpty(english) ? said.English : $"{said.English}. {english}");
    }

    // Literal keys, so the check that every phrase key has its resources finds them.
    private static (Phrase Phrase, string English)? Coded(string? code) => code switch
    {
        "not-set-up" => (Phrase.Of("ProviderStatus.NotSetUp"), "the account isn't entered yet; enter it on the provider's page"),
        "needs-sign-in" => (Phrase.Of("ProviderStatus.NeedsSignIn"), "sign in on the provider's page"),
        "needs-code" => (Phrase.Of("ProviderStatus.NeedsCode"), "enter the code you were sent, on the provider's page"),
        "unreachable" => (Phrase.Of("ProviderStatus.Unreachable"), "its service can't be reached"),
        "rate-limited" => (Phrase.Of("ProviderStatus.RateLimited"), "its service is limiting requests; it tries again later"),
        "refused" => (Phrase.Of("ProviderStatus.Refused"), "its service refused the account or the request"),
        "service-changed" => (Phrase.Of("ProviderStatus.ServiceChanged"), "its service answered in a way it doesn't understand; it may need an update"),
        _ => null,
    };
}
