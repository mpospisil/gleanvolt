using System.Net;
using System.Text;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// Merging deliveries (issue #140), which the reference ID.4 forced.
///
/// <para>The portal sends <c>partial</c> deliveries: each carries the reports that changed, so the
/// newest one alone is a coin toss over which report type arrives. The live one held 47 fields — a
/// real odometer, a real target SOC, doors, climate, settings — and no state of charge anywhere in
/// it, while the car's SOC was perfectly well known to Home Assistant at the same moment.</para>
///
/// <para>Merging needs no new rules: the mapper already takes several snapshots, filters sentinels
/// first and lets the newest real value win. What is new is <b>how many deliveries are fetched</b>,
/// and that is adaptive — older ones only while the reading still has no state of charge.</para>
/// </summary>
public class VwGroupMergedReadTests
{
    private const string Portal = "https://portal.test";
    private const string Vin = "WVWZZZE2ZMP012345";

    private static VwGroupPortalOptions Options(int budget = 4) => new()
    {
        PortalBaseUrl = Portal,
        IdentityBaseUrl = "https://identity.test",
        ClientId = "brand-client-id",
        Username = "owner@example.com",
        Password = "hunter2",
        Timeout = TimeSpan.FromSeconds(5),
        MaxDatasetsPerRead = budget,
    };

    /// <summary>A signed-in portal offering named datasets, each with its own bundle.</summary>
    private sealed class Deliveries(params (string Name, string CreatedOn, byte[] Zip)[] datasets)
        : HttpMessageHandler
    {
        public List<string> Downloaded { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/consent/me/vehicles", StringComparison.Ordinal))
            {
                return Task.FromResult(Json($$"""[{"vin":"{{Vin}}"}]"""));
            }

            if (path.Contains("/datarequest/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"Identifier":"request-1"}"""));
            }

            if (path.EndsWith("/list", StringComparison.Ordinal))
            {
                var items = datasets.Select(dataset =>
                    $$"""{"name":"{{dataset.Name}}","createdOn":"{{dataset.CreatedOn}}"}""");

                return Task.FromResult(Json($"[{string.Join(",", items)}]"));
            }

            if (path.EndsWith("/download", StringComparison.Ordinal))
            {
                var name = request.Headers.GetValues("filename").Single();
                Downloaded.Add(name);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(datasets.Single(dataset => dataset.Name == name).Zip),
                });
            }

            return Task.FromResult(Json("{}", HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>A delivery in the shape the portal really sends: one report, named fields, its own clock.</summary>
    private static byte[] Delivery(string capturedAt, params (string Field, string Value)[] fields)
    {
        var readings = fields
            .Select(field => $$"""{"dataFieldName":"{{field.Field}}","value":"{{field.Value}}"}""")
            .Prepend($$"""{"dataFieldName":"car_captured_time","value":"{{capturedAt}}"}""");

        return VwGroupFixtures.BundleOf(
            ($"report-{capturedAt.Replace(":", "-")}.json", $$"""{"Data":[{{string.Join(",", readings)}}]}"""));
    }

    /// <summary>The live case: a status delivery on top, the battery one behind it.</summary>
    private static Deliveries TheReferenceCar() => new(
        ("status-latest.zip", "2026-09-02T10:30:00Z", Delivery(
            "2026-09-02T10:29:46Z",
            ("mileage.value", "53065"),
            ("settings.target_soc", "80"),
            ("locked", "true"))),
        ("battery-earlier.zip", "2026-09-02T10:15:00Z", Delivery(
            "2026-09-02T10:14:12Z",
            ("battery_level_HV.value", "57"),
            ("cruising_range_combined", "310"))));

    private static VwGroupPortalClient Client(HttpMessageHandler transport, VwGroupPortalOptions options)
    {
        var http = new HttpClient(transport);
        return new VwGroupPortalClient(http, options);
    }

    [Fact]
    public async Task A_delivery_without_the_battery_pulls_the_one_before_it()
    {
        using var portal = TheReferenceCar();
        var read = await Client(portal, Options()).ReadAsync();

        Assert.Equal(57, read.Mapping.State!.SocPercent);
        Assert.Equal(310, read.Mapping.State.RangeKm);

        // The odometer from the newest, the battery from the one before: one car out of two deliveries.
        Assert.Equal(53065, read.Mapping.OdometerKm);
        Assert.Equal(2, read.DatasetsRead);
    }

    [Fact]
    public async Task The_reading_is_dated_by_what_contributed_it_not_by_the_newest_delivery()
    {
        // The honesty that merging makes load-bearing. A state of charge from 10:14 stamped with the
        // 10:29 status report's clock is a stale reading wearing a fresh face -- and how fresh a
        // reading is, is the one thing this feed exists to let somebody judge.
        using var portal = TheReferenceCar();
        var read = await Client(portal, Options()).ReadAsync();

        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 10, 14, 12, TimeSpan.Zero),
            read.Mapping.State!.CapturedAt);
    }

    [Fact]
    public async Task A_delivery_that_carries_the_battery_costs_exactly_one_download()
    {
        // The common case must not become four ZIPs over a domestic uplink every quarter of an hour.
        using var portal = new Deliveries(
            ("battery-latest.zip", "2026-09-02T10:30:00Z", Delivery(
                "2026-09-02T10:29:46Z", ("battery_level_HV.value", "57"))),
            ("older.zip", "2026-09-02T10:15:00Z", Delivery(
                "2026-09-02T10:14:12Z", ("battery_level_HV.value", "56"))));

        var read = await Client(portal, Options()).ReadAsync();

        Assert.Equal(57, read.Mapping.State!.SocPercent);
        Assert.Single(portal.Downloaded);
    }

    [Fact]
    public async Task The_budget_is_a_ceiling_and_the_read_stops_at_it()
    {
        using var portal = new Deliveries(
            ("a.zip", "2026-09-02T10:30:00Z", Delivery("2026-09-02T10:29:00Z", ("locked", "true"))),
            ("b.zip", "2026-09-02T10:15:00Z", Delivery("2026-09-02T10:14:00Z", ("locked", "true"))),
            ("c.zip", "2026-09-02T10:00:00Z", Delivery("2026-09-02T09:59:00Z", ("locked", "true"))));

        var read = await Client(portal, Options(budget: 2)).ReadAsync();

        Assert.Equal(2, portal.Downloaded.Count);

        // Nothing was found, and the failure says so rather than throwing: the surface that shows it
        // is the one that most needs the field lists.
        Assert.Null(read.Mapping.State);
        Assert.Contains("recognises", read.Mapping.Error);
    }

    [Fact]
    public async Task The_newest_delivery_is_read_first_whatever_order_the_portal_lists_them_in()
    {
        // Ordering is not something to inherit on trust when a wrong choice silently ages a reading.
        using var portal = new Deliveries(
            ("older.zip", "2026-09-02T10:00:00Z", Delivery(
                "2026-09-02T09:59:00Z", ("battery_level_HV.value", "40"))),
            ("newest.zip", "2026-09-02T10:30:00Z", Delivery(
                "2026-09-02T10:29:00Z", ("battery_level_HV.value", "57"))));

        var read = await Client(portal, Options()).ReadAsync();

        Assert.Equal("newest.zip", portal.Downloaded[0]);
        Assert.Equal(57, read.Mapping.State!.SocPercent);
    }

    [Fact]
    public async Task One_unreadable_delivery_does_not_cost_the_read()
    {
        using var portal = new Deliveries(
            ("broken.zip", "2026-09-02T10:30:00Z", "not a zip at all"u8.ToArray()),
            ("good.zip", "2026-09-02T10:15:00Z", Delivery(
                "2026-09-02T10:14:12Z", ("battery_level_HV.value", "57"))));

        var read = await Client(portal, Options()).ReadAsync();

        Assert.Equal(57, read.Mapping.State!.SocPercent);
        Assert.Equal(2, read.DatasetsRead);
    }
    [Fact]
    public async Task The_reading_is_dated_by_the_state_of_charge_not_by_the_freshest_field_beside_it()
    {
        // Measured, not supposed: the reference car offered thirty deliveries and the newest four
        // spanned most of a day, because the portal delivers when the car reports rather than on a
        // tidy clock. So a state of charge really can be hours older than the status report merged
        // beside it, and dating the pair by the newer one shows an old percentage as minutes fresh.
        // The percentage is what a target is computed from and what MaxAge is judged against.
        using var portal = new Deliveries(
            ("status-now.zip", "2026-09-02T10:30:00Z", Delivery(
                "2026-09-02T10:29:46Z", ("mileage.value", "53065"), ("locked", "true"))),
            ("battery-yesterday.zip", "2026-09-01T16:31:00Z", Delivery(
                "2026-09-01T16:30:00Z", ("battery_state_report.soc", "50"))));

        var read = await Client(portal, Options()).ReadAsync();

        Assert.Equal(50, read.Mapping.State!.SocPercent);

        // Yesterday afternoon, which is what it is -- and MaxAge can then do its job.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 1, 16, 30, 0, TimeSpan.Zero),
            read.Mapping.State.CapturedAt);
    }


}
