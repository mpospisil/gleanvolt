using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The UI works with no configuration at all, and these are the facts that make it so: an absent
/// <c>Web</c> section still yields an enabled UI on a known port, and no login is demanded until a
/// password is actually configured. Every one of these is reachable from a deployment that never
/// writes a <c>Web</c> setting.
/// </summary>
public class WebOptionsTests
{
    private static WebOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
    }

    [Fact]
    public void Is_on_when_nothing_is_configured()
    {
        // The whole point of the default: a deployment that says nothing about the web UI still gets
        // one. Home Assistant is the surface that stays opt-in, because it needs a broker and
        // credentials before it can do anything at all.
        var options = Bind();

        Assert.True(options.Enabled);
    }

    [Fact]
    public void Can_be_turned_off_explicitly()
    {
        var options = Bind(("Web:Enabled", "false"));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Defaults_to_port_8090()
    {
        var options = Bind();

        Assert.Equal(8090, options.Port);
    }

    [Fact]
    public void Takes_the_port_from_configuration()
    {
        var options = Bind(("Web:Enabled", "true"), ("Web:Port", "9443"));

        Assert.Equal(9443, options.Port);
    }

    [Fact]
    public void Requires_no_login_by_default()
    {
        // Setting a password is the advanced option, so its absence cannot be an error -- an
        // unconfigured UI has to serve pages rather than demand a sign-in nobody can satisfy.
        var options = Bind();

        Assert.False(options.AuthenticationRequired);
    }

    [Fact]
    public void Requires_a_login_as_soon_as_a_password_hash_is_configured()
    {
        // The inference that makes the password a single setting rather than a pair: no separate
        // switch to remember, so a configured password can never be silently unenforced.
        var options = Bind(("Web:PasswordHash", "AQAAAAIAAYagAAAAE..."));

        Assert.True(options.AuthenticationRequired);
        Assert.Equal("AQAAAAIAAYagAAAAE...", options.PasswordHash);
    }

    [Fact]
    public void Treats_a_whitespace_only_password_hash_as_no_password()
    {
        // An env var that ended up empty is the realistic version of this -- WEB_PASSWORD_HASH= in
        // .env, or the compose default. It must not flip the login on and lock everybody out.
        var options = Bind(("Web:PasswordHash", "   "));

        Assert.False(options.AuthenticationRequired);
    }

    [Fact]
    public void Lets_an_explicit_setting_override_the_inference_in_both_directions()
    {
        var forcedOn = Bind(("Web:RequireAuthentication", "true"), ("Web:PasswordHash", "hash"));
        var forcedOff = Bind(("Web:RequireAuthentication", "false"), ("Web:PasswordHash", "hash"));

        Assert.True(forcedOn.AuthenticationRequired);
        Assert.False(forcedOff.AuthenticationRequired);
    }

    [Fact]
    public void Treats_an_empty_RequireAuthentication_as_unset()
    {
        // docker-compose.yml passes Web__RequireAuthentication: ${WEB_REQUIRE_AUTHENTICATION:-},
        // which reaches the container as an empty string rather than as an absent variable. If that
        // bound to false instead of null it would defeat the inference for every containerised
        // deployment -- a configured password would stop being enforced.
        var options = Bind(("Web:RequireAuthentication", ""), ("Web:PasswordHash", "hash"));

        Assert.Null(options.RequireAuthentication);
        Assert.True(options.AuthenticationRequired);
    }

    [Fact]
    public void Requires_no_login_when_the_section_exists_but_says_nothing_about_authentication()
    {
        // The shape appsettings.json actually ships: a Web section that exists, with Enabled and
        // Port in it and no authentication keys at all. Distinct from Bind() with no section --
        // there the binder returns null and never touches the object -- so this is the case that
        // decides what a real deployment does, and the one worth pinning.
        var options = Bind(("Web:Enabled", "true"), ("Web:Port", "8090"));

        Assert.Null(options.RequireAuthentication);
        Assert.Equal("", options.PasswordHash);
        Assert.False(options.AuthenticationRequired);
    }

    [Fact]
    public void Has_no_password_hash_configured_by_default()
    {
        var options = Bind();

        Assert.Equal("", options.PasswordHash);
    }

    [Fact]
    public void Binds_from_the_double_underscore_environment_form_the_deploy_stack_uses()
    {
        // How the container is actually configured: compose sets Web__Enabled / Web__Port, never a
        // json file. Worth a test because the section name is the only thing tying the two together.
        Environment.SetEnvironmentVariable("Web__Enabled", "true");
        Environment.SetEnvironmentVariable("Web__Port", "9090");

        try
        {
            var options = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build()
                .GetSection(WebOptions.SectionName)
                .Get<WebOptions>();

            Assert.NotNull(options);
            Assert.True(options.Enabled);
            Assert.Equal(9090, options.Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Web__Enabled", null);
            Environment.SetEnvironmentVariable("Web__Port", null);
        }
    }
}
