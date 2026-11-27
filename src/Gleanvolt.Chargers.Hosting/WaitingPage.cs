using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Gleanvolt.Providers.Client;

namespace Gleanvolt.Chargers;

/// <summary>
/// What the plugin's page shows until the owner accepts it in Gleanvolt (#459): its name, where the pairing stands,
/// the code to compare with Gleanvolt's, and the addresses it can be found at. It is open to the whole network, so it
/// shows nothing else, escapes everything it shows, and reloads itself to move on once paired.
/// </summary>
internal static class WaitingPage
{
    /// <summary>The page, in <paramref name="culture"/>'s language.</summary>
    public static string Html(PluginInfo plugin, PairingStatus status, IEnumerable<IPAddress> addresses, CultureInfo culture)
    {
        var czech = culture.TwoLetterISOLanguageName == "cs";
        string Say(Words words) => Encode(czech ? words.Cs : words.En);

        var phase = status.Phase switch
        {
            PairingPhase.Waiting => SharedText.PairingWaiting,
            PairingPhase.Rejected => SharedText.PairingRejected,

            // NotNeeded with no token is the moment after a paired token was dropped: the search starts again.
            _ => SharedText.PairingSearching,
        };

        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"""
            <!DOCTYPE html>
            <html lang="{(czech ? "cs" : "en")}" data-theme="auto">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <meta http-equiv="refresh" content="10">
            <link rel="stylesheet" href="/_content/Gleanvolt.Chargers.Hosting/app.css">
            <title>{Encode(plugin.Name)}</title>
            </head>
            <body>
            <h1>{Encode(plugin.Name)}</h1>
            <p id="pairing-phase" class="{(status.Phase == PairingPhase.Rejected ? "error" : "muted")}">{Say(phase)}</p>

            """);

        if (status.Code.Length > 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"""
                <dl>
                <dt>{Say(SharedText.PairingCode)}</dt>
                <dd id="pairing-code"><strong>{Encode(status.Code)}</strong></dd>
                </dl>

                """);
        }

        var own = addresses.Select(address => address.ToString()).ToList();
        if (own.Count > 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<h2>{Say(SharedText.PairingAddresses)}</h2>\n<ul id=\"pairing-addresses\">\n");
            foreach (var address in own)
            {
                html.Append(CultureInfo.InvariantCulture, $"<li>{Encode(address)}</li>\n");
            }

            html.Append("</ul>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    // Every letter as it is, so the Czech reads in the source too; the markup's own characters escaped.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static string Encode(string text) => Encoder.Encode(text);
}
