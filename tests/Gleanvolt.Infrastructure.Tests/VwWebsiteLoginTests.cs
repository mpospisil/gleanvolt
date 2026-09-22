using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Infrastructure.Vehicles.VwWebsite;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// Reading where a volkswagen.de login has got to (issue #170). Every case here was seen against the
/// live identity provider rather than imagined.
/// </summary>
public class VwWebsiteLoginTests
{
    [Fact]
    public void The_credential_form_is_recognised_and_its_state_captured()
    {
        var page = VwWebsiteLoginPage.Read("https://identity.vwgroup.io/u/login?state=abc123", null);

        Assert.Equal(VwWebsiteLoginStep.CredentialsRequired, page.Step);
        Assert.Equal("abc123", page.State);
    }

    /// <summary>The ordinary path on a cold login, not an exception to it.</summary>
    [Fact]
    public void The_code_challenge_is_recognised()
    {
        var page = VwWebsiteLoginPage.Read(
            "https://identity.vwgroup.io/u/mfa-email-challenge?state=xyz",
            "<form><input name=\"state\" value=\"formstate\"><input name=\"code\"></form>");

        Assert.Equal(VwWebsiteLoginStep.OneTimeCodeRequired, page.Step);

        // The form's own state wins over the URL's: it is what the provider wants posted back.
        Assert.Equal("formstate", page.State);
    }

    [Fact]
    public void Landing_on_the_portal_is_what_signed_in_looks_like()
    {
        var page = VwWebsiteLoginPage.Read(
            "https://www.volkswagen.de/de/besitzer-und-nutzer/myvolkswagen.html", "<html></html>");

        Assert.Equal(VwWebsiteLoginStep.SignedIn, page.Step);
    }

    /// <summary>
    /// The failure produced by splitting the flow across two cookie jars. It reads as a credential
    /// problem and is not one — the session was simply lost mid-flow.
    /// </summary>
    [Fact]
    public void A_lost_session_is_a_failure_rather_than_a_sign_in()
    {
        var page = VwWebsiteLoginPage.Read(
            "https://identity.vwgroup.io/v2/login/ui/error?error=invalid_request"
            + "&error_description=we%20couldn%27t%20find%20your%20session", null);

        Assert.Equal(VwWebsiteLoginStep.Failed, page.Step);
    }

    [Fact]
    public void A_refused_code_is_told_apart_from_a_lost_session()
    {
        const string rejected =
            "<span id=\"error-element-code\" data-error-code=\"invalid-code\">The code you entered is invalid</span>";

        Assert.True(VwWebsiteLoginPage.IsRejectedCode(rejected));
        Assert.False(VwWebsiteLoginPage.IsRejectedCode("<html>anything else</html>"));
    }
}

/// <summary>
/// Keeping the session across restarts (issue #170) — the difference between a code once and a code
/// every restart. What protects the bytes is <c>FileSecretStore</c>'s business now (issue #215) and is
/// tested there; what is tested here is that a cookie jar survives the round trip.
/// </summary>
public class VwWebsiteSessionStoreTests : IDisposable
{
    private readonly FileSecretStore _secrets = new(
        Path.Combine(Path.GetTempPath(), $"gleanvolt-vw-session-{Guid.NewGuid():N}"));

    [Fact]
    public void A_saved_jar_comes_back()
    {
        var jar = new CookieContainer();
        jar.Add(new Cookie("csrf_token", "abc", "/", "www.volkswagen.de"));
        jar.Add(new Cookie("auth0", "xyz", "/", "identity.vwgroup.io"));

        var store = new VwWebsiteSessionStore(_secrets);
        Assert.True(store.Save(jar));

        var loaded = store.Load().GetAllCookies();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, cookie => cookie.Name == "csrf_token" && cookie.Value == "abc");
    }

    /// <summary>An expired cookie is not worth restoring, and restoring it would mask a lapsed session.</summary>
    [Fact]
    public void An_expired_cookie_is_dropped_on_load()
    {
        var jar = new CookieContainer();
        jar.Add(new Cookie("stale", "v", "/", "www.volkswagen.de")
        {
            Expires = DateTime.UtcNow.AddDays(-1),
        });

        var store = new VwWebsiteSessionStore(_secrets);
        store.Save(jar);

        Assert.Empty(store.Load().GetAllCookies());
    }

    [Fact]
    public void No_saved_session_is_an_empty_jar_rather_than_a_failure()
    {
        var store = new VwWebsiteSessionStore(
            new FileSecretStore(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}")));

        Assert.False(store.Exists);
        Assert.Empty(store.Load().GetAllCookies());
    }

    [Fact]
    public void An_unreadable_session_is_an_empty_jar_rather_than_a_crash()
    {
        _secrets.Write(SecretNames.VwWebsiteSession, "{ not json at all");

        Assert.Empty(new VwWebsiteSessionStore(_secrets).Load().GetAllCookies());
    }

    [Fact]
    public void Signing_out_forgets_it()
    {
        var jar = new CookieContainer();
        jar.Add(new Cookie("csrf_token", "abc", "/", "www.volkswagen.de"));

        var store = new VwWebsiteSessionStore(_secrets);
        store.Save(jar);
        store.Clear();

        Assert.False(store.Exists);
        Assert.False(File.Exists(_secrets.PathFor(SecretNames.VwWebsiteSession)));
    }

    /// <summary>These cookies are bearer-equivalent: whoever holds the file is signed in as the owner.</summary>
    [Fact]
    public void The_session_lands_in_an_owner_only_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var jar = new CookieContainer();
        jar.Add(new Cookie("csrf_token", "abc", "/", "www.volkswagen.de"));
        new VwWebsiteSessionStore(_secrets).Save(jar);

        var mode = File.GetUnixFileMode(_secrets.PathFor(SecretNames.VwWebsiteSession));

        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
    }

    public void Dispose()
    {
        if (Directory.Exists(_secrets.Directory))
        {
            Directory.Delete(_secrets.Directory, recursive: true);
        }
    }
}
