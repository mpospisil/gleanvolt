using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The switch that stops a second copy of an installation spending the first one's quota.
///
/// <para>Solcast's free tier allows ten calls a day and the quota belongs to the <b>site</b>: a
/// workstation running the same configuration takes them from the controller on the roof, and the
/// symptom is a <c>429</c> there rather than an error here. These tests pin the two properties that
/// make the switch worth having — that it makes no call, and that it is checked <i>before</i> the
/// credentials, so a working key can stay where it is.</para>
/// </summary>
public class SolcastEnabledTests
{
    /// <summary>A transport that fails the test if anything reaches it.</summary>
    private sealed class ForbiddenTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException(
                $"Solcast was called at {request.RequestUri}, which is the entire thing this switch prevents.");
        }
    }

    private sealed class OneClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.solcast.com.au/") };
    }

    private static SolcastForecastService Service(SolcastOptions options, HttpMessageHandler transport) =>
        new(new OneClientFactory(transport), Options.Create(options), NullLogger<SolcastForecastService>.Instance);

    private static SolcastOptions Configured(bool enabled) => new()
    {
        Enabled = enabled,
        ApiKey = "a-real-looking-key",
        ResourceId = "2eb1-1208-081d-bd74",
    };

    [Fact]
    public async Task Switched_off_it_makes_no_call_even_though_it_is_fully_configured()
    {
        using var transport = new ForbiddenTransport();
        var service = Service(Configured(enabled: false), transport);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task The_switch_is_read_before_the_credentials_so_a_working_key_can_stay_put()
    {
        // The alternative -- deleting the key to stop the calls -- turns a deliberate choice into
        // something indistinguishable from a misconfiguration, warns about it on every refresh, and
        // has to be undone before the key can ever be used again.
        using var transport = new ForbiddenTransport();
        var options = Configured(enabled: false);

        Assert.False(string.IsNullOrWhiteSpace(options.ApiKey));
        Assert.False(string.IsNullOrWhiteSpace(options.ResourceId));

        await Service(options, transport).RefreshAsync(CancellationToken.None);

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Switched_off_it_still_serves_whatever_it_has_rather_than_throwing()
    {
        // The forecast is advisory: a plan with none degrades, and nothing on a hardware path may
        // depend on it. Turning the feed off must not turn into an exception anywhere.
        using var transport = new ForbiddenTransport();
        var service = Service(Configured(enabled: false), transport);

        await service.RefreshAsync(CancellationToken.None);

        Assert.Null(service.GetForecastForToday());
        Assert.Null(service.ExpectedPowerWattsNow(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void It_is_on_unless_somebody_turns_it_off()
    {
        // A deployment must not lose its forecast because a flag was added.
        Assert.True(new SolcastOptions().Enabled);
    }
}
