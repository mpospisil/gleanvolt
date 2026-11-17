namespace Gleanvolt.Core.Models;

/// <summary>
/// The ranges the three day-to-day numbers may be set to at runtime — minimum battery SOC, SOC resume
/// margin and min solar surplus — from any door: the web UI, a Home Assistant number, or the HTTP API
/// (issue #392). One place, so a value one door accepts is never refused by another.
/// </summary>
public static class RuntimeSettingLimits
{
    public const double MinBatterySocPercentMin = 0;
    public const double MinBatterySocPercentMax = 100;

    public const double SocResumeMarginPercentMin = 0;
    public const double SocResumeMarginPercentMax = 50;

    public const double MinSolarSurplusWattsMin = 0;
    public const double MinSolarSurplusWattsMax = 20000;
}
