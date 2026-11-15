using System.Globalization;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Resources;

namespace Gleanvolt.Web.Components;

/// <summary>
/// One kind's session in a sentence (#386): the kind's own panel and the External providers section
/// (#406) say it the same way.
/// </summary>
public static class ExternalProviderStateText
{
    /// <summary>The sentence, with times in <paramref name="zone"/>.</summary>
    public static string Describe(ExternalProviderStatus status, TimeZoneInfo zone) => status.State switch
    {
        ExternalProviderState.Off => SettingsText.ExternalState_Off,
        ExternalProviderState.Waiting => status.HasToken ? SettingsText.ExternalState_Waiting : SettingsText.ExternalState_NoToken,
        ExternalProviderState.Connected => Localized.Format(SettingsText.ExternalState_Connected,
            status.ProviderName ?? status.ProviderId ?? string.Empty, status.ProviderVersion ?? string.Empty, When(status.Since, zone)),
        ExternalProviderState.Disconnected => Localized.Format(SettingsText.ExternalState_Disconnected,
            status.ProviderName ?? status.ProviderId ?? string.Empty, When(status.Since, zone)),
        _ => string.Empty,
    };

    /// <summary>An instant as the provider sections show it: date and time in the site's zone.</summary>
    public static string When(DateTimeOffset? at, TimeZoneInfo zone) =>
        at is { } instant
            ? TimeZoneInfo.ConvertTime(instant, zone).ToString(SharedText.DatePattern + " HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;
}
