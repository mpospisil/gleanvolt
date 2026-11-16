using Microsoft.AspNetCore.Http.Connections.Client;

namespace Gleanvolt.Providers.Client;

/// <summary>How a provider finds Gleanvolt and introduces itself.</summary>
public sealed class ProviderOptions
{
    /// <summary>Gleanvolt's web address, e.g. <c>http://gleanvolt.local:8090</c>. The hub path is added to it.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The token generated for this kind on Gleanvolt's Settings.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Stable across restarts: lowercase letters, digits and dashes.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>What Gleanvolt shows the owner.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The provider's own version. Defaults to the entry assembly's.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// The provider's own page, for the owner (#410), e.g. <c>http://192.168.2.7:8095/</c>: Gleanvolt shows
    /// it in a frame and links to it. It must be reachable from the owner's browser, not only from
    /// Gleanvolt. Protect it with <c>app.UseGleanvoltProviderPage()</c>, which admits only a browser
    /// Gleanvolt sent. Null when the provider has no page.
    /// </summary>
    public Uri? PageUrl { get; set; }

    /// <summary>The first wait after a failed connection. It doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest wait between connection attempts.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The wait after Gleanvolt refused the hello: the kind is off, or another provider is connected.
    /// Longer than a network retry, since it changes only when somebody changes a setting.
    /// </summary>
    public TimeSpan RefusedRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>For tests and unusual networks: adjusts the underlying HTTP connection.</summary>
    public Action<HttpConnectionOptions>? ConfigureHttp { get; set; }
}
