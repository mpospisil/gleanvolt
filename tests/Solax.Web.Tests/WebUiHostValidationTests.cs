namespace Solax.Web.Tests;

/// <summary>
/// <see cref="WebUiHost.ValidateAuthenticationConfig"/> is the fail-fast that stops the host rather
/// than silently serving an unprotected UI -- see <c>WebOptions.RequireAuthentication</c>.
/// </summary>
public class WebUiHostValidationTests
{
    [Fact]
    public void Refuses_to_start_when_auth_is_required_but_no_hash_is_set()
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
    public void Allows_starting_with_no_hash_when_the_ui_itself_is_disabled()
    {
        // RequireAuthentication defaults to true, so an installation that never turns the UI on at
        // all must not be forced to also configure a password for it.
        var options = new WebOptions { Enabled = false, RequireAuthentication = true, PasswordHash = "" };

        var exception = Record.Exception(options.ValidateAuthenticationConfig);

        Assert.Null(exception);
    }
}
