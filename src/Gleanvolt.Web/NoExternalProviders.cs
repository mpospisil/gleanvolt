using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Web;

/// <summary>A host with no external provider kinds registered (#386): nothing to show, nothing to make.</summary>
internal sealed class NoExternalProviders : IExternalProviders
{
    public IReadOnlyList<ExternalProviderStatus> All => [];

    public Task<string> CreateTokenAsync(string kind) =>
        throw new InvalidOperationException($"There is no external provider kind '{kind}' in this host.");

    public string? ReadToken(string kind) => null;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
