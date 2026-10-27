using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The Solcast account on Settings (issue #286). A save is a fetch — exactly one, since that is the
/// check — and an incomplete account spends nothing. Removing the page's account hands back to the
/// environment's, and with none to hand back to, the held forecast goes too.
/// </summary>
public class SolcastAccountEditorTests
{
    private static readonly string Forecast =
        """{"forecasts":[{"pv_estimate":4.0,"period_end":"2026-08-09T11:00:00Z","period":"PT30M"}]}""";

    private readonly CountingHandler _handler = new();

    private (SolcastAccountEditor Editor, SolcastForecastService Service) Create(SolcastOptions? environment = null)
    {
        var options = Options.Create(environment ?? new SolcastOptions());
        var store = new SolcastAccountStore(new InMemorySecretStore(), options);
        var service = new SolcastForecastService(
            new Factory(_handler), store, NullLogger<SolcastForecastService>.Instance,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 8, 9, 6, 0, 0, TimeSpan.Zero)));

        return (new SolcastAccountEditor(store, service, NullLogger<SolcastAccountEditor>.Instance), service);
    }

    [Fact]
    public async Task ASaveFetchesOnceWithTheNewAccount()
    {
        var (editor, _) = Create();

        var result = await editor.SaveAsync(" page-key ", " page-site ");

        Assert.True(result.Saved);
        Assert.Equal(1, _handler.Calls);
        Assert.Equal("page-key", _handler.LastKey);
        Assert.Contains("rooftop_sites/page-site/", _handler.LastUri);

        var settings = editor.Read();
        Assert.Equal(SolcastAccountSource.Page, settings.Source);
        Assert.Equal("page-site", settings.ResourceId);
        Assert.NotNull(settings.Status.FetchedAt);
    }

    [Theory]
    [InlineData("", "site")]
    [InlineData("key", " ")]
    public async Task AnIncompleteAccountIsRefusedWithoutACall(string key, string site)
    {
        var (editor, _) = Create();

        var result = await editor.SaveAsync(key, site);

        Assert.False(result.Saved);
        Assert.Single(result.Errors);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(SolcastAccountSource.None, editor.Read().Source);
    }

    [Fact]
    public async Task AnEmptyKeyKeepsTheKeyInUse()
    {
        var (editor, _) = Create(new SolcastOptions { ApiKey = "env-key", ResourceId = "env-site" });

        var result = await editor.SaveAsync(string.Empty, "other-site");

        Assert.True(result.Saved);
        Assert.Equal("env-key", _handler.LastKey);
        Assert.Equal(SolcastAccountSource.Page, editor.Read().Source);
    }

    [Fact]
    public async Task RemovingWithNoEnvironmentDropsTheForecastAndSpendsNothing()
    {
        var (editor, service) = Create();
        await editor.SaveAsync("page-key", "page-site");

        editor.Remove();

        Assert.Equal(1, _handler.Calls);
        Assert.Equal(SolcastAccountSource.None, editor.Read().Source);
        Assert.Null(service.GetForecast(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task RemovingHandsBackToTheEnvironmentAndKeepsTheForecast()
    {
        var (editor, service) = Create(new SolcastOptions { ApiKey = "env-key", ResourceId = "env-site" });
        await editor.SaveAsync("page-key", "page-site");
        var changes = 0;
        editor.Changed += () => changes++;

        editor.Remove();

        var settings = editor.Read();
        Assert.Equal(SolcastAccountSource.Environment, settings.Source);
        Assert.Equal("env-site", settings.ResourceId);
        Assert.NotNull(service.GetForecast(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task EveryFetchIsNewsToThePage()
    {
        var (editor, service) = Create(new SolcastOptions { ApiKey = "env-key", ResourceId = "env-site" });
        var changes = 0;
        editor.Changed += () => changes++;

        await service.RefreshAsync();

        Assert.Equal(1, changes);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastKey { get; private set; }

        public string LastUri { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastKey = request.Headers.Authorization?.Parameter;
            LastUri = request.RequestUri?.ToString() ?? string.Empty;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Forecast, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri(new SolcastOptions().BaseUrl) };
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Read(string name) => _values.GetValueOrDefault(name);

        public bool Write(string name, string value)
        {
            _values[name] = value;
            return true;
        }

        public bool Delete(string name) => _values.Remove(name) || true;

        public string Describe() => "memory";
    }
}
