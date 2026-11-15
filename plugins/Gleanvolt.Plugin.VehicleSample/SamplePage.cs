using System.Net;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;

namespace Gleanvolt.Plugin.VehicleSample;

/// <summary>
/// The sample's own page (#410), shown in Gleanvolt's Car page: the pretend car's SOC, and buttons to
/// fake an expired login and to sign in again. Plain HTML and two form posts, so the frame can be tried
/// without a real car; the Volkswagen plugin's page is interactive Blazor.
///
/// <para>Each form keeps the query string Gleanvolt opened the page with, ticket included: in the frame,
/// on a host other than Gleanvolt's, the browser may not send the SDK's cookie, and the ticket is then
/// what admits the post, for as long as it is valid.</para>
/// </summary>
public static class SamplePage
{
    public static void MapSamplePage(this WebApplication app)
    {
        app.MapGet("/", (HttpContext context, SimulatedCar car, SampleLogin login, VehicleProviderConnection connection) =>
            Results.Content(Render(context, car, login, connection), "text/html; charset=utf-8"));

        // Not form-bound, so no antiforgery is needed: the SDK's ticket check is what admits a post.
        app.MapPost("/expire", async (HttpContext context, SampleLogin login) =>
        {
            await login.ExpireAsync("The car account's login expired (simulated on the sample's page). Sign in again there.", context.RequestAborted);
            return Results.Redirect("/" + context.Request.QueryString);
        });

        app.MapPost("/sign-in", async (HttpContext context, SampleLogin login) =>
        {
            await login.SignInAsync(context.RequestAborted);
            return Results.Redirect("/" + context.Request.QueryString);
        });
    }

    private static string Render(HttpContext context, SimulatedCar car, SampleLogin login, VehicleProviderConnection connection)
    {
        var query = context.Request.QueryString.Value ?? string.Empty;
        var theme = context.Request.Query[ProviderPage.ThemeParameter].ToString() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "light dark",
        };

        var reading = connection.Car is { } known ? car.Reading(known) : null;
        var soc = reading?.SocPercent is { } percent ? $"{percent:0} %" : "not known until Gleanvolt welcomes the sample";
        var state = !connection.IsAttached ? "Not connected to Gleanvolt."
            : login.Expired ? "The pretend login has expired: Gleanvolt shows that you are needed."
            : $"Connected. Captured {reading?.CapturedAt:u}.";
        var button = login.Expired
            ? $"<form method=\"post\" action=\"/sign-in{H(query)}\"><button>Sign in again</button></form>"
            : $"<form method=\"post\" action=\"/expire{H(query)}\"><button>Fake an expired login</button></form>";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Vehicle sample</title>
            <style>
              :root { color-scheme: {{theme}}; font-family: system-ui, sans-serif; }
              body { margin: 1rem; }
              .soc { font-size: 2rem; font-weight: 600; }
              button { font: inherit; padding: .4rem .9rem; }
            </style>
            </head>
            <body>
            <p>The pretend car of the vehicle sample, charging from 40 % to 80 %.</p>
            <p class="soc" id="soc">{{H(soc)}}</p>
            <p id="state">{{H(state)}}</p>
            {{button}}
            </body>
            </html>
            """;
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);
}
