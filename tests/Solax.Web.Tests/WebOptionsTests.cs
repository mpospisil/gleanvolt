using Microsoft.Extensions.Configuration;

namespace Solax.Web.Tests;

/// <summary>
/// The UI is optional by construction, and these are the two facts that make it so: an absent
/// <c>Web</c> section leaves it off, and turning it on does not also require choosing a port.
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
    public void Is_off_when_nothing_is_configured()
    {
        var options = Bind();

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Defaults_to_port_8080()
    {
        var options = Bind(("Web:Enabled", "true"));

        Assert.True(options.Enabled);
        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void Takes_the_port_from_configuration()
    {
        var options = Bind(("Web:Enabled", "true"), ("Web:Port", "9443"));

        Assert.Equal(9443, options.Port);
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
