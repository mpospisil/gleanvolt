using System.Text.RegularExpressions;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;

namespace Gleanvolt.Providers.Testing;

/// <summary>The checks Gleanvolt makes on every hello, before it looks at which provider is connected.</summary>
internal static partial class HelloRules
{
    public static (string? Code, string? Reason) Check(ProviderHello? hello)
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

        return (null, null);
    }

    // Gleanvolt's own pattern (ProviderHub).
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ProviderId();
}
