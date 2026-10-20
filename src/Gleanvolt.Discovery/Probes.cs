using System.Net.Sockets;
using System.Text;

namespace Gleanvolt.Discovery;

/// <summary>Whether a TCP port accepts a connection.</summary>
public interface IPortProbe
{
    Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// A plain TCP connect, closed at once. No raw sockets, no ICMP and no ARP, so it needs no privileges
/// and works the same on Windows, Linux and inside a container (see DECISIONS.md, 2026-10-20).
/// </summary>
public sealed class TcpPortProbe : IPortProbe
{
    public async Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>The text of a host's web page, for telling a SolaX dongle from every router on the LAN.</summary>
public interface IWebProbe
{
    /// <summary>The response's headers and the start of its body, or null when nothing answered HTTP.</summary>
    Task<string?> ReadAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>One <c>GET /</c>, nothing else: reading a page changes nothing on the device.</summary>
public sealed class HttpWebProbe : IWebProbe
{
    // The start of the page is enough to find a name in, and a page is not worth more of a scan.
    private const int MaxCharacters = 64 * 1024;

    public async Task<string?> ReadAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = timeout };

        try
        {
            using var response = await http.GetAsync(
                new Uri($"http://{host}:{port}/"), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var text = new StringBuilder();

            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                text.Append(header.Key).Append(": ").AppendJoin(", ", header.Value).Append('\n');
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(body);
            var buffer = new char[MaxCharacters];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);

            return text.Append(buffer, 0, read).ToString();
        }
        // HttpClient reports its own timeout as a cancellation; only the caller's is passed on.
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}

/// <summary>What a web page says about the box serving it.</summary>
public static class SolaxWebPage
{
    /// <summary>
    /// Whether <paramref name="page"/> is a SolaX dongle's. The charger serves a page too, mentioning
    /// <c>chargeweb</c> (see SUPPORTED_PV_SYSTEMS.md); that one is not a dongle, and the charger will
    /// be found on its Modbus port. Unconfirmed against a Pocket WiFi's actual page: a dongle that
    /// never says "SolaX" is simply not reported, which costs nothing but the hint.
    /// </summary>
    public static bool IsDongle(string? page) =>
        page is not null
        && page.Contains("solax", StringComparison.OrdinalIgnoreCase)
        && !page.Contains("chargeweb", StringComparison.OrdinalIgnoreCase);
}
