using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Tariff;
using Gleanvolt.Hosting.Tariff.External;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Tariff;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Tariff;
using Gleanvolt.Providers.Server;
using Gleanvolt.Plugin.TariffSample;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ContractWindow = Gleanvolt.Providers.Contracts.Tariff.TariffWindow;
using TariffRate = Gleanvolt.Core.Models.TariffRate;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The low tariff as an external provider (#401), end to end: the real hub, schedule and refresh worker
/// on a TestServer, and the tariff sample as the provider, connecting the way a provider author's would.
/// </summary>
public sealed class ExternalTariffHubTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-external-tariff-" + Guid.NewGuid().ToString("N"));

    // 10:00 in Prague on a Sunday: high tariff in the sample's pattern, low from 13:00.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));

    private WebApplication? _app;
    private string _token = string.Empty;

    private TariffSettingsFile Settings => _app!.Services.GetRequiredService<TariffSettingsFile>();

    private TariffSchedule Schedule => _app!.Services.GetRequiredService<TariffSchedule>();

    private ExternalProviderRegistry Registry => _app!.Services.GetRequiredService<ExternalProviderRegistry>();

    private ExternalProviderStatus Status => Registry.Find(TariffContract.Kind)!;

    public async Task InitializeAsync()
    {
        _time.SetLocalTimeZone(Prague);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<ISecretStore>(new RecordingSecretStore());
        builder.Services.AddSingleton(new ProviderHostInfo("1.3.0-test"));
        builder.Services.AddSingleton(provider => new ExternalTariffProvider(new HubTariffRequests(provider, _time)));
        builder.Services.AddSingleton(provider => new TariffSettingsFile(
            Path.Combine(_root, "tariff.json"),
            "CZK",
            [new NoTariffProvider(), new TableTariffProvider(), provider.GetRequiredService<ExternalTariffProvider>()],
            NullLogger<TariffSettingsFile>.Instance));
        builder.Services.AddSingleton<ITariffEditor>(provider => provider.GetRequiredService<TariffSettingsFile>());
        builder.Services.AddSingleton<TariffSchedule>();
        builder.Services.AddSingleton<TariffRefreshWorker>();
        builder.Services.AddExternalProvider<TariffHub>(TariffHub.Kind);
        builder.Services.AddHostedService<ExternalTariffSelectionWatcher>();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExternalProviders();
        await _app.StartAsync();

        Assert.True(Settings.Save(TariffSettings.None with { Provider = TariffProviderIds.External }).Saved);
        _token = await Registry.CreateTokenAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheSampleConnectsLearnsTheSiteAndItsWindowsBecomeTheTariff()
    {
        using var sample = Sample();
        TariffWelcome? welcome = null;
        sample.Welcomed += site => welcome = site;

        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Schedule.Timeline is not null, "the sample's windows should become the timeline");

        Assert.Equal("Europe/Prague", welcome!.TimeZone);
        Assert.Equal("CZK", welcome.Currency);

        var timeline = Schedule.Timeline!;
        Assert.Equal(TariffProviderIds.External, timeline.ProviderId);
        Assert.Equal("Tariff sample", timeline.Origin!.Name);
        Assert.Equal(TariffRate.High, timeline.RateAt(_time.GetUtcNow()));
        Assert.Equal(At(13), timeline.NextChangeAfter(_time.GetUtcNow()));
        Assert.Equal(ExternalProviderState.Connected, Status.State);
        Assert.Equal(_time.GetUtcNow(), Status.LastDataAt);

        await sample.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheRefreshWorkerAsksAndTheSampleAnswersQuotingTheRequest()
    {
        using var sample = Sample();
        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Status.LastDataAt is not null, "the sample should publish after its hello");

        _time.Advance(TimeSpan.FromMinutes(10));
        var wait = await _app!.Services.GetRequiredService<TariffRefreshWorker>().RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(1), wait);
        await Eventually(() => Status.LastDataAt == _time.GetUtcNow(), "the sample should answer the request");

        _time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(100);
        Assert.False(Status.Unanswered);

        await sample.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARequestWithNoAnswerShowsAsUnansweredAndTheWorkerIsNotHeld()
    {
        await using var silent = await ConnectAsync();
        Assert.True((await silent.InvokeAsync<TariffWelcome>("Hello", Hello())).Accepted);

        var refresh = _app!.Services.GetRequiredService<TariffRefreshWorker>().RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(1), await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        _time.Advance(HubTariffRequests.Timeout + TimeSpan.FromSeconds(1));
        await Eventually(() => Status.Unanswered, "the request should time out");
    }

    [Fact]
    public async Task RefusedDataChangesNothingAndSaysWhy()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<TariffWelcome>("Hello", Hello());
        var now = _time.GetUtcNow();

        var result = await connection.InvokeAsync<PublishResult>("Publish", new TariffData(now, null, "CZK",
        [
            new ContractWindow(now.AddHours(-1), now.AddHours(1), Providers.Contracts.Tariff.TariffRate.Low),
            new ContractWindow(now.AddHours(2), now.AddHours(3), Providers.Contracts.Tariff.TariffRate.High),
        ]));

        Assert.False(result.Accepted);
        Assert.Contains("gap", Assert.Single(result.Problems));
        Assert.Null(Schedule.Timeline);
        Assert.Equal("ExternalTariff.Gap", Status.LastProblemPhrases![0]!.Key);
    }

    [Fact]
    public async Task EnumsTravelByName()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<TariffWelcome>("Hello", Hello());
        var now = _time.GetUtcNow();

        // What a client in another language sends: the rate as a string.
        var result = await connection.InvokeAsync<PublishResult>("Publish", new
        {
            issuedAt = now,
            currency = "CZK",
            windows = new[] { new { start = now.AddHours(-1), end = now.AddHours(5), rate = "low", pricePerKwh = (decimal?)1.5m } },
        });

        Assert.True(result.Accepted, string.Join(" ", result.Problems));
        Assert.Equal(TariffRate.Low, Schedule.Timeline!.RateAt(now));
        Assert.Equal(1.5m, Schedule.Timeline.PriceAt(now));
    }

    [Fact]
    public async Task AfterADisconnectTheWindowsCountUntilTheLastOneEnds()
    {
        using var sample = Sample();
        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Schedule.Timeline is not null, "the sample should publish");
        var lastEnd = Schedule.Timeline!.Windows[^1].Window.End;

        await sample.StopAsync(CancellationToken.None);
        await Eventually(() => Status.State == ExternalProviderState.Disconnected, "the session should end");

        Assert.NotNull(Schedule.Timeline);

        _time.Advance(lastEnd - _time.GetUtcNow() + TimeSpan.FromMinutes(1));

        Assert.Null(Schedule.Timeline);
        Assert.NotNull(Schedule.Unavailable);
    }

    [Fact]
    public async Task ChoosingAnotherTariffClosesTheSessionAndRefusesTheNextHello()
    {
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<TariffWelcome>("Hello", Hello());
        var goodbye = new TaskCompletionSource<string>();
        connection.On<string, string>("Goodbye", (code, _) => goodbye.TrySetResult(code));

        Assert.True(Settings.Save(TariffSettingsFileTests.Night()).Saved);

        Assert.Equal(RefusalCodes.Disabled, await goodbye.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ExternalProviderState.Off, Status.State);

        await using var again = await ConnectAsync();
        Assert.Equal(RefusalCodes.Disabled, (await again.InvokeAsync<TariffWelcome>("Hello", Hello())).ReasonCode);
    }

    private TariffProviderConnection Sample() => new(
        Options.Create(new ProviderOptions
        {
            Endpoint = _app!.GetTestServer().BaseAddress,
            Token = _token,
            ProviderId = "tariff-sample",
            Name = "Tariff sample",
            Version = "1.0",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            RefusedRetryDelay = TimeSpan.FromMilliseconds(100),
            ConfigureHttp = Configure,
        }),
        new SampleTariff(_time),
        NullLogger<TariffProviderConnection>.Instance);

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_app!.GetTestServer().BaseAddress, TariffContract.HubPath), Configure)
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private void Configure(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => _app!.GetTestServer().CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
    }

    private static ProviderHello Hello() => new("raw-client", "Raw client", "0.1", TariffContract.Major, TariffContract.Minor);

    private static DateTimeOffset At(int hour) => new(2026, 11, 15, hour, 0, 0, TimeSpan.FromHours(1));

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), because);
    }
}
