namespace Gleanvolt.Devices.Testing;

/// <summary>
/// What every EV charger driver reached over HTTP is held to (issue #352): the shared tests of
/// <see cref="ChargerConformanceTests{TFactory, TRecording}"/> on <see cref="HttpDump"/>s, and the snapshot's.
/// A request the driver makes that is not marked as a read (<see cref="ChargerHttp.Read"/>) counts as a write,
/// so "reading writes nothing" also holds the driver to the mark the host's dry-run relies on.
/// </summary>
/// <typeparam name="TFactory">The driver's factory.</typeparam>
public abstract class HttpEvChargerDriverConformanceTests<TFactory> : ChargerConformanceTests<TFactory, HttpDump>
    where TFactory : IEvChargerDriverFactory
{
    protected override IChargerReplay Replay(HttpDump recording) => new HttpChargerReplay(new ReplayHttpHandler(recording));

    protected override string Describe(HttpDump recording) =>
        string.IsNullOrEmpty(recording.Note) ? $"The {recording.Family} dump" : $"The {recording.Family} dump ({recording.Note})";

    // A web server that has nothing at the path answers 404, which a driver may surface as an exception.
    protected override bool IsRefusal(Exception exception) => exception is HttpRequestException or InvalidOperationException;

    private HttpSnapshotSpec Snapshot =>
        CreateFactory().HttpSnapshot ?? throw new InvalidOperationException("A charger reached over HTTP names its snapshot requests.");

    [Fact]
    public void The_family_is_reached_over_http_and_names_its_snapshot()
    {
        var factory = CreateFactory();

        Assert.Equal(EvChargerTransport.Http, factory.Transport);
        Assert.NotNull(factory.HttpSnapshot);
        Assert.Null(factory.Snapshot);
        Assert.NotEmpty(Snapshot.Requests);
    }

    [Fact]
    public async Task The_snapshot_covers_every_request_the_driver_reads()
    {
        foreach (var dump in Recordings)
        {
            var replay = new HttpChargerReplay(new ReplayHttpHandler(dump));
            var driver = CreateDriver(replay);

            await driver.ReadAsync();
            await driver.ReadSettingsAsync();
            await driver.ProbeAsync();

            var missing = replay.Handler.Reads.Distinct().Except(Snapshot.Requests.Select(request => request.TrimStart('/'))).ToList();
            Assert.True(missing.Count == 0, "The snapshot does not read: " + string.Join(", ", missing));
        }
    }

    [Fact]
    public async Task A_snapshot_replays_the_driver()
    {
        var factory = CreateFactory();
        using var client = new HttpClient(new ReplayHttpHandler(Charging)) { BaseAddress = new Uri("http://charger.test/") };
        var snapshot = await HttpSnapshot.TakeAsync(client, Snapshot, new HttpDump { Driver = factory.Id });

        var original = CreateDriver(Replay(Charging));
        var replayed = CreateDriver(Replay(HttpDump.FromJson(snapshot.ToJson())));

        Assert.Equal(await original.ReadAsync(), await replayed.ReadAsync());
        Assert.Equal(await original.ReadSettingsAsync(), await replayed.ReadSettingsAsync());
    }

    /// <summary>Loads a dump from a fixture file.</summary>
    protected static HttpDump Load(string path) => HttpDump.FromJson(File.ReadAllText(path));
}
