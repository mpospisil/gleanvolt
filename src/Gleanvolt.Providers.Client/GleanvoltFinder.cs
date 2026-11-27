using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client;

/// <summary>Where a provider without a token looks for a Gleanvolt to ask to be paired with (#459).</summary>
public interface IGleanvoltFinder
{
    /// <summary>
    /// Gleanvolt addresses to try, most likely first. Pairing knocks on each until one answers, so a later one
    /// is looked for only when the earlier ones did not answer.
    /// </summary>
    IAsyncEnumerable<Uri> CandidatesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The SDK's search, used only for pairing: the configured <see cref="ProviderOptions.Endpoint"/>, then
/// <c>http://localhost:8090</c> (Gleanvolt on this machine, the common case), then every Gleanvolt that
/// answers an mDNS browse for <c>_gleanvolt._tcp</c>. Once paired, a provider talks only to the Gleanvolt that
/// accepted it, so the token never goes to whatever else answers on the LAN.
/// </summary>
public sealed class GleanvoltFinder(IOptions<ProviderOptions> options) : IGleanvoltFinder
{
    private static readonly Uri ThisMachine = new("http://localhost:8090");

    public async IAsyncEnumerable<Uri> CandidatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tried = new HashSet<Uri>();

        if (options.Value.Endpoint is { } configured && tried.Add(configured))
        {
            yield return configured;
        }

        if (tried.Add(ThisMachine))
        {
            yield return ThisMachine;
        }

        foreach (var found in await MdnsBrowser.BrowseAsync(MdnsBrowser.Timeout, cancellationToken).ConfigureAwait(false))
        {
            if (tried.Add(found))
            {
                yield return found;
            }
        }
    }
}
