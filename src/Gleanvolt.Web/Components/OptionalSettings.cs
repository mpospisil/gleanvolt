using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>What each optional setting is called, what it adds, and where it is set (#302).</summary>
internal static class OptionalSettings
{
    public static string Name(OptionalSetting setting) => setting switch
    {
        OptionalSetting.Solcast => SetupText.OptionalSolcast,
        OptionalSetting.Ev => SetupText.OptionalEv,
        OptionalSetting.Tariff => SetupText.OptionalTariff,
        OptionalSetting.Mqtt => SetupText.OptionalMqtt,
        _ => SetupText.OptionalWeather,
    };

    public static string Adds(OptionalSetting setting) => setting switch
    {
        OptionalSetting.Solcast => SetupText.OptionalSolcastAdds,
        OptionalSetting.Ev => SetupText.OptionalEvAdds,
        OptionalSetting.Tariff => SetupText.OptionalTariffAdds,
        OptionalSetting.Mqtt => SetupText.OptionalMqttAdds,
        _ => SetupText.OptionalWeatherAdds,
    };

    // The wizard's step that sets each one.
    public static string? Where(OptionalSetting setting) => setting switch
    {
        // On Settings and the Car page, where they live (#442): the wizard sets up only Gleanvolt itself.
        OptionalSetting.Solcast => "/settings#solcast-editor",
        OptionalSetting.Ev => "/car#car-editor",
        OptionalSetting.Tariff => "/settings#tariff-editor",
        OptionalSetting.Mqtt => "/settings#mqtt-editor",
        OptionalSetting.Weather => "/settings#weather-editor",
        _ => null,
    };
}
