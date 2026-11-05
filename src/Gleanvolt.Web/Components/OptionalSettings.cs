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

    // The wizard's step for what has a form; MQTT and the weather are set in .env and have none yet.
    public static string? Where(OptionalSetting setting) => setting switch
    {
        OptionalSetting.Solcast => "/wizard/solcast",
        OptionalSetting.Ev => "/wizard/car",
        OptionalSetting.Tariff => "/wizard/extras",
        OptionalSetting.Mqtt or OptionalSetting.Weather => "/wizard/extras",
        _ => null,
    };
}
