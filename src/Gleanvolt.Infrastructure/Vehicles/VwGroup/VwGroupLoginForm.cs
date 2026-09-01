using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// One page of the identity provider's sign-in flow, as data: where its form posts to, what it wants
/// carried along, and whether it is a page a program can answer at all.
///
/// <para>Pure, and therefore the part of sign-in that is actually tested. The transport is
/// <see cref="VwGroupSignIn"/>; everything hard about the flow — which hidden fields exist, where
/// they are hidden, and whether the page in front of us is a consent screen — is decided here against
/// a captured string.</para>
///
/// <para><b>Two places to look, because the identity provider uses both.</b> The visible form carries
/// its inputs as HTML; <c>hmac</c>, <c>_csrf</c> and <c>relayState</c> also appear in a
/// <c>templateModel</c> JSON blob in a script tag, and which of the two is authoritative has changed
/// before. Hidden inputs win when both are present, and the blob fills whatever they left out.</para>
///
/// <para><b>Regex rather than an HTML parser</b>, deliberately: this reads two known-shaped login
/// pages on a Raspberry Pi, and the alternative is a parsing dependency shipped to every installation
/// to serve one class. It is not a general HTML reader and must not become one.</para>
/// </summary>
public sealed record VwGroupLoginForm(
    string? Action,
    string Method,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> FieldTypes)
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly Regex FormTag = new("<form\\b[^>]*>", Options);
    private static readonly Regex InputTag = new("<input\\b[^>]*>", Options);
    private static readonly Regex Attribute = new("([\\w:-]+)\\s*=\\s*(\"([^\"]*)\"|'([^']*)')", Options);
    private static readonly Regex TemplateModel =
        new("templateModel\\s*[:=]\\s*(\\{.*?\\})\\s*[,;<]", Options);

    /// <summary>
    /// Words that mean a human is required. Matched against the whole page, lower-cased.
    ///
    /// <para>The three that end #137's unattended design outright are the first three. Consent and
    /// terms are listed separately because they are recoverable by the owner once, in a browser,
    /// rather than being a permanent obstacle — and a message that says which one appeared is the
    /// difference between an actionable failure and a shrug.</para>
    /// </summary>
    private static readonly (string Reason, string[] Needles)[] OwnerActionNeedles =
    [
        ("a CAPTCHA", ["captcha", "recaptcha", "hcaptcha", "turnstile"]),
        ("a one-time code", ["one-time password", "one time password", "verification code", "security code", "einmalcode"]),
        ("two-factor authentication", ["two-factor", "two factor", "authenticator app"]),
        ("a consent screen", ["consent", "einwilligung"]),
        ("terms to accept", ["terms and conditions", "accept the terms", "nutzungsbedingungen"]),
    ];

    /// <summary>Whether there is a form here at all to post.</summary>
    public bool IsPostable => !string.IsNullOrWhiteSpace(Action);

    /// <summary>
    /// The first form on the page, with every input it carries and everything the template model
    /// adds. Fields are returned verbatim so they can be replayed: the identity provider decides what
    /// it wants back, and naming that list in code would break silently the day it gains a sixth
    /// member.
    /// </summary>
    public static VwGroupLoginForm Parse(string? html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(html))
        {
            return new VwGroupLoginForm(null, "post", fields, types);
        }

        var form = FormTag.Match(html);
        string? action = null;
        var method = "post";

        if (form.Success)
        {
            var attributes = ReadAttributes(form.Value);
            attributes.TryGetValue("action", out action);

            if (attributes.TryGetValue("method", out var declared) && !string.IsNullOrWhiteSpace(declared))
            {
                method = declared.ToLowerInvariant();
            }

            // Inputs from the start of the form to the end of the document rather than to </form>:
            // the closing tag is routinely absent from generated markup, and an input that belongs to
            // a later form is harmless here (this page has one).
            foreach (Match input in InputTag.Matches(html, form.Index))
            {
                var attributes2 = ReadAttributes(input.Value);

                if (!attributes2.TryGetValue("name", out var name) || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                fields[name] = attributes2.GetValueOrDefault("value", string.Empty);
                types[name] = attributes2.GetValueOrDefault("type", "text").ToLowerInvariant();
            }
        }

        foreach (var (name, value) in ReadTemplateModel(html))
        {
            // Only what the markup left out. A hidden input the page actually renders is the value
            // the browser would have posted, and that is the one to replay.
            if (!fields.TryGetValue(name, out var existing) || string.IsNullOrEmpty(existing))
            {
                fields[name] = value;
                types.TryAdd(name, "hidden");
            }
        }

        return new VwGroupLoginForm(action, method, fields, types);
    }

    /// <summary>
    /// The field to put the identifier or the password in, found by name or by input type so a rename
    /// on the identity provider's side does not need a release here.
    /// </summary>
    public string? FieldFor(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            foreach (var (name, type) in FieldTypes)
            {
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>The identifier field, whatever this build of the login page calls it.</summary>
    public string? IdentifierField => FieldFor("identifier", "email", "username");

    /// <summary>The password field.</summary>
    public string? PasswordField => FieldFor("password");

    /// <summary>
    /// Why a human is needed on this page, or null when it is one a program can answer.
    ///
    /// <para>Checked <b>before</b> the form is posted, not after it fails: posting a password into a
    /// consent screen tells the identity provider nothing and tells us nothing either.</para>
    /// </summary>
    public static string? OwnerActionReason(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var lowered = html.ToLowerInvariant();

        foreach (var (reason, needles) in OwnerActionNeedles)
        {
            if (needles.Any(needle => lowered.Contains(needle, StringComparison.Ordinal)))
            {
                return reason;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Attribute.Matches(tag))
        {
            var value = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
            attributes[match.Groups[1].Value] = System.Net.WebUtility.HtmlDecode(value);
        }

        return attributes;
    }

    // The identity provider's own state, as a JSON object inside a script tag. Only string values are
    // taken: everything replayed goes into a form post, and a nested object has no representation
    // there.
    private static IEnumerable<KeyValuePair<string, string>> ReadTemplateModel(string html)
    {
        var match = TemplateModel.Match(html);

        if (!match.Success)
        {
            yield break;
        }

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(match.Groups[1].Value).RootElement;
        }
        catch (JsonException)
        {
            // A blob we cannot read is not a failure: the hidden inputs are the primary source, and
            // this was only ever the fallback.
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                yield return new KeyValuePair<string, string>(property.Name, property.Value.GetString() ?? string.Empty);
            }
        }
    }
}
