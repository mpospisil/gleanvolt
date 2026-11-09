using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;

namespace Gleanvolt.Infrastructure.Http;

/// <summary>
/// The <see cref="HttpClient"/> a charger reached over HTTP is driven through (go-e, issue #352), as
/// <c>ModbusTcpClient</c> is for Modbus: one per charger, owned by the host, its base address the charger.
/// </summary>
public static class ChargerHttpClients
{
    // The charger answers on the LAN within a second; five leave room for a busy one, and still fit the poll.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A client for the charger at <paramref name="device"/>'s host and port. Unless <paramref name="writable"/>,
    /// only requests the driver marked as reads (<see cref="ChargerHttp.Read"/>) reach the charger.
    /// </summary>
    public static HttpClient Create(DeviceConfig device, bool writable, ILogger<ReadOnlyHttpHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(device);

        // Connections are recycled every few minutes so a charger that changed address behind the same name
        // (DHCP) is found again without a restart.
        HttpMessageHandler handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = RequestTimeout,
        };

        if (!writable)
        {
            handler = new ReadOnlyHttpHandler(logger) { InnerHandler = handler };
        }

        return new HttpClient(handler)
        {
            BaseAddress = BaseAddress(device),
            Timeout = RequestTimeout,
        };
    }

    /// <summary>A client that refuses every request: setup mode, where no address has been confirmed.</summary>
    public static HttpClient NotConfigured() =>
        new(new NotConfiguredHttpHandler()) { BaseAddress = new Uri("http://not-configured.invalid/") };

    /// <summary><c>http://host:port/</c>.</summary>
    public static Uri BaseAddress(DeviceConfig device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new UriBuilder(Uri.UriSchemeHttp, device.Host.Trim(), device.Port, "/").Uri;
    }

    private sealed class NotConfiguredHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The charger is not configured yet: finish setup first.");
    }
}

/// <summary>
/// Makes an HTTP client incapable of writing to a charger, as <c>ReadOnlyModbusClient</c> does for Modbus: a
/// request the driver marked as a read (<see cref="ChargerHttp.IsRead"/>) passes, and anything else is dropped
/// with a warning and answered with an empty JSON object. The driver already skips its writes in dry-run, so a
/// dropped write means a driver missed its guard: the warning is a tripwire, not a routine message.
/// </summary>
public sealed class ReadOnlyHttpHandler(ILogger<ReadOnlyHttpHandler> logger) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ChargerHttp.IsRead(request))
        {
            return base.SendAsync(request, cancellationToken);
        }

        logger.LogWarning(
            "Dry-run: suppressed {Method} {Path}. Nothing was sent to the charger, but a caller should have skipped this write.",
            request.Method, ChargerHttp.PathAndQuery(request));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
    }
}
