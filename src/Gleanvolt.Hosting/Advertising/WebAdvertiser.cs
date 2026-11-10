using System.Net.Sockets;
using System.Text.RegularExpressions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Infrastructure.Mdns;
using Gleanvolt.Web;

namespace Gleanvolt.Hosting.Advertising;

/// <summary>
/// Announces the web UI on the LAN over multicast DNS (issue #363): <c>gleanvolt.local</c> for the
/// machine, and the UI as an <c>_http._tcp</c> service (for any Bonjour/DNS-SD browser) and a
/// <c>_gleanvolt._tcp</c> one (for our own tools). It runs in setup mode too — that is when the user
/// most needs to find the machine — and its TXT record says which: <c>setup=pending</c> or <c>done</c>.
///
/// <para>From inside the process, so it is the same on every install: the .deb, Docker with the host
/// network, a NAS, Windows. In a container on a bridge network multicast never reaches the LAN, and
/// the advertisement goes unheard; nothing else is affected.</para>
///
/// <para>It never stops the host. If UDP 5353 cannot be opened, it says so once and gives up: the UI is
/// reachable by address all the same.</para>
/// </summary>
public sealed partial class WebAdvertiser : BackgroundService
{
    public const string HttpService = "_http._tcp";
    public const string GleanvoltService = "_gleanvolt._tcp";

    private readonly WebOptions _web;
    private readonly ISetupState _setup;
    private readonly ILogger<WebAdvertiser> _logger;
    private readonly Func<IMdnsTransport> _transport;

    public WebAdvertiser(IOptions<WebOptions> web, ISetupState setup, ILogger<WebAdvertiser> logger)
        : this(web.Value, setup, logger, () => new UdpMdnsTransport())
    {
    }

    internal WebAdvertiser(WebOptions web, ISetupState setup, ILogger<WebAdvertiser> logger, Func<IMdnsTransport> transport)
    {
        _web = web;
        _setup = setup;
        _logger = logger;
        _transport = transport;
    }

    /// <summary>
    /// What is announced. Nothing in the TXT record beyond what a finder needs: no site data, no serial
    /// numbers, no host details the address record does not already give.
    /// </summary>
    public static MdnsAdvertisement Describe(string hostLabel, int port, bool setupMode, string version) => new(
        hostLabel,
        InstanceName(hostLabel),
        (ushort)port,
        [HttpService, GleanvoltService],
        ["path=/", $"version={version}", $"setup={(setupMode ? "pending" : "done")}"]);

    /// <summary>
    /// <c>AdvertiseName</c> as a DNS label: lower case, letters, digits and inner hyphens, at most 63
    /// characters. Null when it cannot be one.
    /// </summary>
    public static string? HostLabel(string? name)
    {
        var label = name?.Trim().ToLowerInvariant();
        return label is not null && Label().IsMatch(label) ? label : null;
    }

    private static string InstanceName(string hostLabel) =>
        hostLabel == WebOptions.DefaultAdvertiseName ? "Gleanvolt" : $"Gleanvolt {hostLabel}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var label = HostLabel(_web.AdvertiseName);
        if (label is null)
        {
            _logger.LogWarning(
                "Web:AdvertiseName '{Name}' cannot be a .local name (letters, digits and hyphens only); advertising as {Default}.local.",
                _web.AdvertiseName,
                WebOptions.DefaultAdvertiseName);
            label = WebOptions.DefaultAdvertiseName;
        }

        IMdnsTransport transport;
        try
        {
            transport = _transport();
        }
        catch (SocketException exception)
        {
            _logger.LogWarning(
                "Not advertising on the LAN: UDP {MdnsPort} could not be opened ({Reason}). The web UI is on port {Port} all the same.",
                UdpMdnsTransport.MdnsPort,
                exception.Message,
                _web.Port);
            return;
        }

        await using (transport)
        {
            var responder = new MdnsResponder(transport, Describe(label, _web.Port, _setup.SetupMode, BuildInfo.Version), _logger)
            {
                Claimed = (host, instance) => _logger.LogInformation(
                    "Advertised on the LAN as http://{Host}:{Port} (\"{Instance}\", mDNS).", host, _web.Port, instance),
            };

            await responder.RunAsync(stoppingToken);
        }
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex Label();
}
