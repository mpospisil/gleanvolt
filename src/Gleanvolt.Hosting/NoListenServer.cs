using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;

namespace Gleanvolt.Hosting;

/// <summary>
/// The server used when the web UI is switched off: it accepts nothing, binds nothing and opens no
/// socket.
///
/// <para>The host is an ASP.NET one because it may have to serve the UI, but with <c>Web:Enabled</c>
/// false the process must be indistinguishable from the headless worker it was before. Simply not
/// mapping any endpoint would not achieve that — Kestrel with no configured endpoint falls back to
/// its default address and starts listening anyway, which on a LAN appliance means an open port the
/// operator never asked for. Registering this in Kestrel's place removes the server itself, so the
/// only things running are the hosted services.</para>
/// </summary>
internal sealed class NoListenServer : IServer
{
    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
        where TContext : notnull => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
