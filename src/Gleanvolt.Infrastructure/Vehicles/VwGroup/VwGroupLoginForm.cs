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
    IReadOnlyDictionary<string, string> FieldTypes,
    string? PostAction = null)
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly Regex FormTag = new("<form\\b[^>]*>", Options);
    private static readonly Regex InputTag = new("<input\\b[^>]*>", Options);
    private static readonly Regex Attribute = new("([\\w:-]+)\\s*=\\s*(\"([^\"]*)\"|'([^']*)')", Options);
    private static readonly Regex Scripts =
        new("<(script|style)\\b.*?</\\1>", Options);

    /// <summary>
    /// The CSRF token, which the identity provider ships as a <b>JavaScript variable</b> rather than
    /// in the template model or (on a client-rendered page) as a hidden input:
    /// <c>csrf_parameterName: '_csrf', csrf_token: '...'</c>, single-quoted.
    ///
    /// <para>The identifier page renders <c>_csrf</c> as a hidden input as well, so step one worked
    /// without this and hid the gap. The password page renders no inputs at all, so posting without
    /// the token is answered <c>400</c> with a <c>generalErrorBranded</c> page — no form on it, which
    /// surfaced as "a page with no form to post" and looked like a refused password.</para>
    /// </summary>
    private static readonly Regex CsrfToken =
        new("csrf_token\\s*[:=]\\s*['\"]([^'\"]+)['\"]", Options);

    private static readonly Regex CsrfParameterName =
        new("csrf_parameterName\\s*[:=]\\s*['\"]([^'\"]+)['\"]", Options);

    private static readonly Regex TemplateModelStart =
        new("templateModel\\s*[:=]\\s*\\{", Options);

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

    /// <summary>
    /// The identity provider's own explanation, when it answered with an OAuth error object rather
    /// than a page — <c>{"error":"invalid_request","error_description":"Mismatching redirection URI"}</c>
    /// and its like. Null when the body is not one.
    ///
    /// <para>Worth a method of its own because of what it replaces. Such a body has no form in it, so
    /// it used to surface as "the identity provider returned a page with no form to post" — true, and
    /// useless: it points at the sign-in when the request was rejected before sign-in began. The
    /// provider had already said exactly what was wrong and nothing read it.</para>
    /// </summary>
    public static string? OAuthError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart().FirstOrDefault() != '{')
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);

            if (json.RootElement.ValueKind is not JsonValueKind.Object
                || !json.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            var description = json.RootElement.TryGetProperty("error_description", out var text)
                ? text.GetString()
                : null;

            return string.IsNullOrWhiteSpace(description)
                ? error.GetString()
                : $"{error.GetString()} -- {description}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether there is anywhere to post at all — an HTML form's action, or the template model's
    /// <c>postAction</c> when the page renders its form in the browser and ships none in the markup.
    ///
    /// <para>The password page is the second kind: <c>useClientRendering: true</c>, zero
    /// <c>&lt;form&gt;</c> tags, zero <c>&lt;input&gt;</c> tags, and everything needed sitting in the
    /// template model instead. Treating "no form tag" as "no way in" stopped the flow one step short
    /// of signing in.</para>
    /// </summary>
    public bool IsPostable => !string.IsNullOrWhiteSpace(Action) || !string.IsNullOrWhiteSpace(PostAction);

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

        string? postAction = null;

        if (ReadTemplateModel(html) is { } model)
        {
            // Only what the markup left out. A hidden input the page actually renders is the value
            // the browser would have posted, and that is the one to replay.
            foreach (var name in CarriedByTemplateModel)
            {
                if (model.TryGetProperty(name, out var carried)
                    && carried.ValueKind == JsonValueKind.String
                    && (!fields.TryGetValue(name, out var existing) || string.IsNullOrEmpty(existing)))
                {
                    fields[name] = carried.GetString() ?? string.Empty;
                    types.TryAdd(name, "hidden");
                }
            }

            if (model.TryGetProperty("postAction", out var post) && post.ValueKind == JsonValueKind.String)
            {
                postAction = post.GetString();
            }

            // The credential fields themselves, and ONLY on a page that renders none of them in
            // markup.
            //
            // The model carries the same emailPasswordForm on both steps, so taking its names
            // unconditionally would find a "password" field on the identifier page -- where the
            // markup asks for an email -- and the caller prefers a password wherever it finds one.
            // The result would be the password posted to login/identifier: the wrong endpoint, and
            // the credential sent a step early. Markup wins where markup exists.
            var markupAsksForACredential = types.Any(field =>
                CredentialNames.Contains(field.Key, StringComparer.OrdinalIgnoreCase)
                || CredentialNames.Contains(field.Value, StringComparer.OrdinalIgnoreCase));

            if (!markupAsksForACredential
                && model.TryGetProperty("emailPasswordForm", out var credentials)
                && credentials.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in credentials.EnumerateObject())
                {
                    if (property.Name.StartsWith('@'))
                    {
                        continue;
                    }

                    fields.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : string.Empty);
                    types.TryAdd(property.Name, property.Name);
                }
            }
        }

        if (CsrfToken.Match(html) is { Success: true } csrf)
        {
            var name = CsrfParameterName.Match(html) is { Success: true } parameter
                ? parameter.Groups[1].Value
                : "_csrf";

            // A hidden input the page actually rendered wins, as everywhere else here.
            if (!fields.TryGetValue(name, out var already) || string.IsNullOrEmpty(already))
            {
                fields[name] = csrf.Groups[1].Value;
                types.TryAdd(name, "hidden");
            }
        }

        return new VwGroupLoginForm(action, method, fields, types, postAction);
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
    /// Whether this page has somewhere to put a credential — an identifier, an email or a password.
    ///
    /// <para>The guard that keeps <see cref="OwnerActionReason"/> honest: <b>a page you can sign in
    /// on is a sign-in page</b>, whatever words are printed on it. A real consent screen has no
    /// password box, so this distinguishes them structurally rather than by vocabulary.</para>
    /// </summary>
    public bool CanSignIn => IdentifierField is not null || PasswordField is not null;

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

        // Scripts first: the identity provider ships a templateModel blob naming the client
        // application, and matching prose needles against machine data is how a page gets called a
        // consent screen for containing its own configuration.
        var lowered = Scripts.Replace(html, " ").ToLowerInvariant();

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
    /// <summary>
    /// The identity provider's <c>templateModel</c> blob, parsed — or null when there is none, or it
    /// cannot be read.
    ///
    /// <para><b>Brace-balanced rather than regex-matched.</b> A lazy <c>\{.*?\}</c> stops at the first
    /// closing brace, which on every real page lands inside the nested
    /// <c>clientLegalEntityModel</c> — 514 characters of truncated, unparseable JSON that was then
    /// silently discarded. The blob has nested objects; only counting braces reads it.</para>
    /// </summary>
    private static JsonElement? ReadTemplateModel(string html)
    {
        var match = TemplateModelStart.Match(html);

        if (!match.Success)
        {
            return null;
        }

        var opening = html.IndexOf('{', match.Index);
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = opening; i < html.Length; i++)
        {
            var c = html[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;

                    if (depth == 0)
                    {
                        try
                        {
                            // Cloned: the document is disposed on the way out and the element must
                            // outlive it.
                            using var json = JsonDocument.Parse(html[opening..(i + 1)]);
                            return json.RootElement.ValueKind == JsonValueKind.Object
                                ? json.RootElement.Clone()
                                : null;
                        }
                        catch (JsonException)
                        {
                            // Unreadable is not a failure: hidden inputs are the primary source and
                            // this was only ever the fallback.
                            return null;
                        }
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// What the template model contributes to the post body.
    ///
    /// <para><b>Named rather than swept up.</b> The blob is a view model, not a form: it also carries
    /// <c>template</c>, <c>titleKey</c>, <c>postAction</c> and <c>identifierUrl</c>, none of which the
    /// browser posts. Taking every string in it would put four junk fields into the credential POST.
    /// These four are what the identity provider actually wants carried back.</para>
    /// </summary>
    private static readonly string[] CarriedByTemplateModel = ["hmac", "relayState", "_csrf", "csrf_token"];

    /// <summary>What a credential field is called, by name or by input type. See the guard in Parse.</summary>
    private static readonly string[] CredentialNames = ["identifier", "email", "username", "password"];
}
