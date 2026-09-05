using System.Text.RegularExpressions;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// What kind of page the identity provider just served, and what it wants posted back (issue #170).
///
/// <para>Pure, and therefore the testable part of a login. The transport is
/// <see cref="VwWebsiteClient"/>; deciding <i>which of four situations we are in</i> happens here
/// against a captured string.</para>
/// </summary>
public sealed record VwWebsiteLoginPage(VwWebsiteLoginStep Step, string? State)
{
    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly Regex StateInput =
        new("<input[^>]*name=\"state\"[^>]*>", Options);

    private static readonly Regex Value = new("value=\"([^\"]*)\"", Options);

    /// <summary>
    /// Reads the page. <paramref name="url"/> decides as much as the body does: the identity provider
    /// signals the step by where it puts you, and the body only confirms it.
    /// </summary>
    public static VwWebsiteLoginPage Read(string url, string? html)
    {
        var step = StepFor(url, html);
        return new VwWebsiteLoginPage(step, StateFrom(html) ?? StateFromUrl(url));
    }

    private static VwWebsiteLoginStep StepFor(string url, string? html)
    {
        if (url.Contains("/u/mfa", StringComparison.OrdinalIgnoreCase))
        {
            // The one that needs a person. Verified against the live account: a cold login always
            // lands here, so this is the ordinary path rather than an exception.
            return VwWebsiteLoginStep.OneTimeCodeRequired;
        }

        if (url.Contains("/u/login", StringComparison.OrdinalIgnoreCase))
        {
            return VwWebsiteLoginStep.CredentialsRequired;
        }

        if (url.Contains("/login/ui/error", StringComparison.OrdinalIgnoreCase)
            || url.Contains("error=", StringComparison.OrdinalIgnoreCase))
        {
            // Includes the one produced by splitting the flow across two cookie jars: "we couldn't
            // find your session". It reads as a credential failure and is not one.
            return VwWebsiteLoginStep.Failed;
        }

        if (url.Contains("consent", StringComparison.OrdinalIgnoreCase))
        {
            return VwWebsiteLoginStep.OwnerActionRequired;
        }

        return html is not null && html.Contains("mfa-email-challenge", StringComparison.OrdinalIgnoreCase)
            ? VwWebsiteLoginStep.OneTimeCodeRequired
            : VwWebsiteLoginStep.SignedIn;
    }

    /// <summary>Whether the code just posted was refused, as opposed to the session being lost.</summary>
    public static bool IsRejectedCode(string? html) =>
        html is not null
        && html.Contains("data-error-code=\"invalid-code\"", StringComparison.OrdinalIgnoreCase);

    private static string? StateFrom(string? html) =>
        html is not null && StateInput.Match(html) is { Success: true } input
        && Value.Match(input.Value) is { Success: true } value
            ? value.Groups[1].Value
            : null;

    private static string? StateFromUrl(string url)
    {
        var marker = url.IndexOf("state=", StringComparison.OrdinalIgnoreCase);

        if (marker < 0)
        {
            return null;
        }

        var from = marker + "state=".Length;
        var to = url.IndexOf('&', from);

        return Uri.UnescapeDataString(to < 0 ? url[from..] : url[from..to]);
    }
}

/// <summary>Where a login attempt has got to.</summary>
public enum VwWebsiteLoginStep
{
    /// <summary>Signed in; the session is usable.</summary>
    SignedIn,

    /// <summary>The credential form is in front of us.</summary>
    CredentialsRequired,

    /// <summary>
    /// An email code is wanted. The owner has to read it — nothing here can, and nothing here should
    /// retry until they have.
    /// </summary>
    OneTimeCodeRequired,

    /// <summary>A consent or terms screen only a browser can answer.</summary>
    OwnerActionRequired,

    /// <summary>Refused, or the session was lost mid-flow.</summary>
    Failed,
}
