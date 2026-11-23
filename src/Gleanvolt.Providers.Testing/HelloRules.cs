using System.Text.RegularExpressions;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Providers.Testing;

/// <summary>The checks Gleanvolt makes on every hello, before it looks at which provider is connected.</summary>
internal static partial class HelloRules
{
    public static (string? Code, string? Reason) Check(ProviderHello? hello, ChargerLimits limits)
    {
        if (hello is null || string.IsNullOrWhiteSpace(hello.Name) || hello.ProviderId is null || !ProviderId().IsMatch(hello.ProviderId))
        {
            return (RefusalCodes.Invalid, "The hello needs a name and an id of lowercase letters, digits and dashes.");
        }

        if (hello.PageUrl is { } page && (!page.IsAbsoluteUri || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps)))
        {
            return (RefusalCodes.Invalid, "The page URL must be an absolute http or https address.");
        }

        if (hello.ContractMajor != ChargerContract.Major)
        {
            return (RefusalCodes.ContractMajor,
                $"The provider speaks contract {hello.ContractMajor}.{hello.ContractMinor}; Gleanvolt speaks {ChargerContract.Major}.{ChargerContract.Minor}.");
        }

        return Fits(hello, limits);
    }

    /// <summary>Gleanvolt's own check of the charger's capabilities (ChargerHub, #440).</summary>
    public static (string? Code, string? Reason) Fits(ProviderHello hello, ChargerLimits limits)
    {
        var capabilities = hello.Charger ?? ChargerCapabilities.Default;

        if (capabilities.Check() is { Count: > 0 } invalid)
        {
            return (RefusalCodes.Invalid, "The charger's capabilities are not usable: " + string.Join(" ", invalid));
        }

        if (ChargerLimitsRules.Check(limits, capabilities, hello.Name) is { Count: > 0 } problems)
        {
            return (RefusalCodes.Limits, string.Join(" ", problems));
        }

        return (null, null);
    }

    // Gleanvolt's own pattern (ProviderHub).
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ProviderId();
}
