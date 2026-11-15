using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Web;

/// <summary>A host with no external provider kinds registered (#386): nothing to show, nothing to make.</summary>
internal sealed class NoExternalProviders : IExternalProviders
{
    public IReadOnlyList<ExternalProviderStatus> All => [];

    public bool HasToken => false;

    public Task<string> CreateTokenAsync() =>
        throw new InvalidOperationException("There are no external provider kinds in this host.");

    public string? ReadToken() => null;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
