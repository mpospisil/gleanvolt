namespace Solax.Web.Tests;

/// <summary>
/// <see cref="WebUiHost.ValidateAuthenticationConfig"/> guards exactly one combination: a login
/// explicitly demanded with no password to check it against, which would leave the UI permanently
/// unreachable. Everything else starts -- see <c>WebOptions.RequireAuthentication</c>.
/// </summary>
public class WebUiHostValidationTests
{
    [Fact]
    public void Refuses_to_start_when_auth_is_explicitly_required_but_no_hash_is_set()
    {
        var options = new WebOptions { Enabled = true, RequireAuthentication = true, PasswordHash = "" };

        Assert.Throws<InvalidOperationException>(options.ValidateAuthenticationConfig);
    }

    [Fact]
    public void Allows_starting_when_auth_is_required_and_a_hash_is_set()
    {
        var options = new WebOptions { Enabled = true, RequireAuthentication = true, PasswordHash = "hash" };

        var exception = Record.Exception(options.ValidateAuthenticationConfig);

        Assert.Null(exception);
    }

    [Fact]
    public void Allows_starting_with_no_hash_when_authentication_is_switched_off()
    {
        var options = new WebOptions { Enabled = true, RequireAuthentication = false, PasswordHash = "" };

        var exception = Record.Exception(options.ValidateAuthenticationConfig);

        Assert.Null(exception);
    }

    [Fact]
    public void Allows_starting_with_no_hash_and_no_explicit_setting()
    {
        // The default deployment: nothing about authentication is configured anywhere, and that has
        // to be a running UI rather than a refusal, or "no configuration required" is not true.
        var options = new WebOptions { Enabled = true };

        var exception = Record.Exception(options.ValidateAuthenticationConfig);

        Assert.Null(exception);
        Assert.False(options.AuthenticationRequired);
    }

    [Fact]
    public void Allows_starting_with_no_hash_when_the_ui_itself_is_disabled()
    {
        // An installation that turns the UI off must not be forced to configure a password for it,
        // even if something left Web:RequireAuthentication switched on.
        var options = new WebOptions { Enabled = false, RequireAuthentication = true, PasswordHash = "" };

        var exception = Record.Exception(options.ValidateAuthenticationConfig);

        Assert.Null(exception);
    }
}
