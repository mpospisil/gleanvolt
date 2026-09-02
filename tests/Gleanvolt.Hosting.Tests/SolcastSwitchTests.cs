using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Solcast's free tier allows ten calls a day and the quota belongs to the <b>site</b>, not to a
/// machine. The controller on the roof spends about five; a workstation running the same
/// configuration spends the rest, and the symptom is a <c>429</c> on the controller hours later —
/// which reads as a bug in whatever was deployed most recently.
///
/// <para>The switch that prevents that is only worth having if it stays on in a deployment and stays
/// off in development, so both halves are asserted here rather than left to a comment.</para>
/// </summary>
public class SolcastSwitchTests
{
    private static readonly string Root = FindRepositoryRoot();

    /// <summary>A transport that fails the test if the worker reaches the network at all.</summary>
    private sealed class ForbiddenTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Solcast was called at {request.RequestUri}.");
    }

    private sealed class OneClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.solcast.com.au/") };
    }

    [Fact]
    public async Task Switched_off_the_refresh_worker_stops_instead_of_looping_over_a_call_it_will_not_make()
    {
        // Stopped rather than slowed: a loop that wakes every three hours to do nothing is a loop
        // somebody has to read the source of to understand.
        var options = new SolcastOptions { Enabled = false, ApiKey = "key", ResourceId = "site" };

        using var transport = new ForbiddenTransport();
        var service = new SolcastForecastService(
            new OneClientFactory(transport), Options.Create(options),
            NullLogger<SolcastForecastService>.Instance);

        var worker = new SolarForecastRefreshWorker(
            service, Options.Create(options), NullLogger<SolarForecastRefreshWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        // It runs to completion on its own rather than being cancelled out of a delay.
        Assert.NotNull(worker.ExecuteTask);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void A_deployment_keeps_its_forecast()
    {
        // The flag was added for development. Defaulting it to anything but "on" would take the
        // forecast away from every installation that never asked for a change.
        Assert.True(new SolcastOptions().Enabled);
        Assert.Contains("\"Enabled\": true", Solcast(File.ReadAllText(
            Path.Combine(Root, "src", "Gleanvolt.Worker", "appsettings.json"))));
    }

    [Fact]
    public void Development_does_not_spend_the_sites_quota()
    {
        // The whole point. A `dotnet run` here must not take a call from the Pi -- and the way that
        // goes wrong is silently, by somebody re-enabling it for an afternoon and not putting it back.
        Assert.Contains("\"Enabled\": false", Solcast(File.ReadAllText(
            Path.Combine(Root, "src", "Gleanvolt.Worker", "appsettings.Development.json"))));
    }

    [Fact]
    public void The_deployment_can_turn_it_off_too_without_deleting_its_key()
    {
        // A second copy of an installation -- a staging box, a container somebody left running -- is
        // the same hazard as a workstation, and it is configured through .env rather than a JSON file.
        var compose = File.ReadAllText(Path.Combine(Root, "deploy", "docker-compose.yml"));

        Assert.Contains("Solcast__Enabled: ${SOLCAST_ENABLED:-true}", compose);
        Assert.Contains("SOLCAST_ENABLED", File.ReadAllText(Path.Combine(Root, "deploy", ".env.example")));
    }

    /// <summary>The Solcast section of a settings file, so a match cannot come from another section.</summary>
    private static string Solcast(string json)
    {
        var start = json.IndexOf("\"Solcast\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The settings file has no Solcast section.");

        var end = json.IndexOf('}', start);
        Assert.True(end > start, "The Solcast section is not closed.");

        return json[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test output directory.");
    }
}
