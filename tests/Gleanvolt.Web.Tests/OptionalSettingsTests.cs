using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// #302's hybrid gate: optional settings never block anything. A page that needs one says it is not
/// configured and where to set it; Health and Settings list what is still missing.
/// </summary>
public class OptionalSettingsTests : BunitContext
{
    private sealed class Setup(bool setupMode, params OptionalSetting[] missing) : ISetupState
    {
        public bool SetupMode => setupMode;

        public SetupStatus AtStartup => Current();

        public SetupStatus Current() => new([], missing);
    }

    [Fact]
    public void A_missing_setting_shows_its_card_with_the_way_to_set_it()
    {
        Services.AddSingleton<ISetupState>(new Setup(false, OptionalSetting.Solcast));

        var card = Render<NotConfiguredCard>(parameters => parameters.Add(c => c.Setting, OptionalSetting.Solcast));

        Assert.Contains("Solar forecast: not configured", card.Markup);
        Assert.Equal("/wizard/solcast", card.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void A_configured_setting_shows_nothing()
    {
        Services.AddSingleton<ISetupState>(new Setup(false, OptionalSetting.Tariff));

        var card = Render<NotConfiguredCard>(parameters => parameters.Add(c => c.Setting, OptionalSetting.Solcast));

        Assert.Empty(card.Markup.Trim());
    }

    [Fact]
    public void Where_the_form_is_on_the_same_page_there_is_no_link()
    {
        Services.AddSingleton<ISetupState>(new Setup(false, OptionalSetting.Ev));

        var card = Render<NotConfiguredCard>(parameters => parameters
            .Add(c => c.Setting, OptionalSetting.Ev)
            .Add(c => c.ShowLink, false));

        Assert.Contains("Car: not configured", card.Markup);
        Assert.Empty(card.FindAll("a"));
    }

    [Fact]
    public void Health_and_settings_list_every_missing_optional_setting()
    {
        Services.AddSingleton<ISetupState>(new Setup(false, OptionalSetting.Mqtt, OptionalSetting.Weather));

        var section = Render<OptionalSettingsSection>();

        Assert.Equal(2, section.FindAll(".optional-checklist li").Count);
        Assert.Contains("Home Assistant (MQTT)", section.Markup);
        Assert.Contains("Weather", section.Markup);
    }

    [Fact]
    public void In_setup_mode_the_setup_notice_speaks_instead()
    {
        Services.AddSingleton<ISetupState>(new Setup(true, OptionalSetting.Mqtt));

        Assert.Empty(Render<OptionalSettingsSection>().Markup.Trim());
    }
}
