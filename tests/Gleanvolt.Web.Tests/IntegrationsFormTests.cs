using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The Home Assistant (MQTT) and API forms on Settings and in the wizard: a save goes to the editor for
/// the next start and says a restart applies it, the password box never shows the password, a broker
/// test uses what the form holds, and an API key is created by name.
/// </summary>
public class IntegrationsFormTests : BunitContext
{
    private readonly FakeIntegrationsEditor _editor = new();

    public IntegrationsFormTests()
    {
        Services.AddSingleton<IIntegrationsEditor>(_editor);
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Saving_the_broker_sends_only_the_changes_and_the_password()
    {
        var form = Render<MqttEditorForm>();

        form.Find("#mqtt-enabled").Change(true);
        form.Find("#mqtt-brokerhost").Input("192.168.1.5");
        form.Find("#mqtt-password").Input("s3cret");
        form.Find("#mqtt-save").Click();

        var (values, password) = Assert.Single(_editor.Saves);
        Assert.Equal("true", values[IntegrationSettingKeys.MqttEnabled]);
        Assert.Equal("192.168.1.5", values[IntegrationSettingKeys.MqttBrokerHost]);
        Assert.Equal(2, values.Count);
        Assert.Equal("s3cret", password);

        // Pending, as the fake's running side has none of it: a restart applies it.
        Assert.Contains("Restart", form.Find("#mqtt-message").TextContent);
        Assert.NotNull(form.Find("#mqtt-pending"));
        Assert.Equal("", form.Find("#mqtt-password").GetAttribute("value") ?? "");
    }

    [Fact]
    public void A_refused_save_lists_why()
    {
        _editor.NextResult = SettingsSaveResult.Refused(["Home Assistant needs the MQTT broker's address."]);
        var form = Render<MqttEditorForm>();

        form.Find("#mqtt-enabled").Change(true);
        form.Find("#mqtt-save").Click();

        Assert.Contains("broker's address", form.Find("#mqtt-problems").TextContent);
    }

    [Fact]
    public void A_test_uses_what_the_form_holds()
    {
        _editor.NextTest = new MqttTestResult(false, "192.168.1.5:1883 answered, and refused the user name or password.");
        var form = Render<MqttEditorForm>();

        form.Find("#mqtt-brokerhost").Input("192.168.1.5");
        form.Find("#mqtt-username").Input("gleanvolt");
        form.Find("#mqtt-test").Click();

        Assert.Equal(("192.168.1.5", 1883, "gleanvolt", (string?)null), Assert.Single(_editor.Tests));
        var result = form.Find("#mqtt-test-result");
        Assert.Contains("refused the user name", result.TextContent);
        Assert.Contains("error", result.ClassList);
        Assert.Empty(_editor.Saves);
    }

    [Fact]
    public void Turning_the_api_on_saves_at_once()
    {
        _editor.Keys.Add(new ApiKeyEntry("claude", "abc", SettingSource.Environment, false));
        var form = Render<ApiEditorForm>();

        form.Find("#api-enabled").Change(true);

        Assert.Equal("true", Assert.Single(_editor.Saves).Values[IntegrationSettingKeys.ApiEnabled]);
        Assert.NotNull(form.Find("#api-pending"));
    }

    [Fact]
    public void A_key_is_created_by_name_and_listed_as_pending()
    {
        var form = Render<ApiEditorForm>();
        Assert.NotNull(form.Find("#api-no-keys"));

        form.Find("#api-key-name").Input("home-assistant");
        form.Find("#api-create-key").Click();

        var row = form.Find("#api-keys tbody tr");
        Assert.Contains("home-assistant", row.TextContent);
        Assert.Contains("next start", row.TextContent);
        Assert.Contains("home-assistant", form.Find("#api-message").TextContent);
    }

    [Fact]
    public void Only_a_key_saved_here_can_be_removed_here()
    {
        _editor.Keys.Add(new ApiKeyEntry("env-client", "abc", SettingSource.Environment, false));
        _editor.Keys.Add(new ApiKeyEntry("page-client", "def", SettingSource.SecretStore, false));
        var form = Render<ApiEditorForm>();

        var rows = form.FindAll("#api-keys tbody tr");
        Assert.Empty(rows[0].QuerySelectorAll("button.link:not(.secret button)").Where(button => button.TextContent.Contains("Remove")));
        rows[1].QuerySelectorAll("button").Single(button => button.TextContent.Contains("Remove")).Click();

        Assert.DoesNotContain(_editor.Keys, key => key.Name == "page-client");
    }

    [Fact]
    public void Without_a_login_both_are_read_only_and_keys_are_not_shown()
    {
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "", RequireAuthentication = false }));
        Services.AddSingleton(new WebPasswordState(null));
        _editor.Keys.Add(new ApiKeyEntry("claude", "the-secret-key", SettingSource.Environment, false));

        var mqtt = Render<MqttEditorForm>();
        var api = Render<ApiEditorForm>();

        Assert.NotNull(mqtt.Find("#mqtt-readonly"));
        Assert.True(mqtt.Find("#mqtt-brokerhost").HasAttribute("disabled"));
        Assert.Empty(mqtt.FindAll("#mqtt-save"));
        Assert.NotNull(api.Find("#api-readonly"));
        Assert.DoesNotContain("the-secret-key", api.Markup);
        Assert.Empty(api.FindAll("#api-create-key"));
    }

    [Fact]
    public void Every_string_comes_from_a_resource()
    {
        _editor.Keys.Add(new ApiKeyEntry("claude", "abc", SettingSource.SecretStore, true));
        _editor.Stored[IntegrationSettingKeys.MqttEnabled] = "true";
        _editor.Password = "x";

        var mqtt = PseudoLocale.Render(() => Render<MqttEditorForm>());
        var api = PseudoLocale.Render(() => Render<ApiEditorForm>());

        PseudoLocale.AssertTranslated(mqtt.Markup, "1883", "homeassistant", "true", "an owner-only file", "Home Assistant (MQTT)");
        PseudoLocale.AssertTranslated(api.Markup, "claude", "API");
    }
}
