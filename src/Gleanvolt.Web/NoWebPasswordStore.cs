using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Web;

/// <summary>
/// The stand-in for a host that wires no persistence, which in practice means a test host.
///
/// <para>It refuses rather than pretending to succeed. A password that appeared to save and then
/// vanished on restart would put the UI back to unauthenticated without anyone being told, which is
/// the exact failure this feature exists to prevent.</para>
/// </summary>
internal sealed class NoWebPasswordStore : IWebPasswordStore
{
    public bool Save(string passwordHash) => false;
}
