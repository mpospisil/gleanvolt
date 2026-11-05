using Gleanvolt.Core.Models;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>Where a setting comes from, in a word, for the forms that save for the next start.</summary>
internal static class SettingSources
{
    public static string Name(ConfiguredSetting setting) =>
        setting.Source == SettingSource.AppSettings && !string.IsNullOrEmpty(setting.SourceDetail)
            ? setting.SourceDetail
            : Name(setting.Source);

    public static string Name(SettingSource source) => source switch
    {
        SettingSource.WebUi => SettingsText.SourceWebUi,
        SettingSource.SecretStore => SettingsText.SourcePage,
        SettingSource.Environment => SettingsText.SourceEnvironment,
        SettingSource.CommandLine => SettingsText.SourceCommandLine,
        SettingSource.AppSettings => "appsettings.json",
        _ => SettingsText.SourceDefault,
    };
}
