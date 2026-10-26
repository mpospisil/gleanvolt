using System.Net.Http.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Tests;

/// <summary>Forecasted <i>until full</i> over the API (issue #279): the flag, and the session running with it.</summary>
public sealed class UntilFullEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task APutSetsTheFlag_AndStartsNothing()
    {
        var client = await _host.StartAsync();

        var answer = await (await client.PutAsJsonAsync("/api/v1/charging/until-full", new { untilFull = true })).ReadAsync();

        Assert.True(answer.GetProperty("untilFull").GetBoolean());
        Assert.True(_host.UntilFull.UntilFull);
        Assert.Equal([$"API ({ApiTestHost.KeyName})"], _host.UntilFull.Sources);

        var read = await (await client.GetAsync("/api/v1/charging/until-full")).ReadAsync();
        Assert.True(read.GetProperty("untilFull").GetBoolean());
    }

    [Fact]
    public async Task TheSessionAndTheStatusNameTheDay()
    {
        var client = await _host.StartAsync();
        var since = new DateTimeOffset(2026, 1, 14, 10, 0, 0, TimeSpan.FromHours(1));
        _host.Status.Set(Fixtures.Status() with
        {
            Mode = ChargeControlMode.Forecasted,
            UntilFull = new UntilFullStatus(since, Day: 2),
        });

        var flag = await (await client.GetAsync("/api/v1/charging/until-full")).ReadAsync();
        var status = await (await client.GetAsync("/api/v1/status")).ReadAsync();

        Assert.Equal(2, flag.GetProperty("session").GetProperty("day").GetInt32());
        Assert.Equal(2, status.GetProperty("untilFull").GetProperty("day").GetInt32());
        Assert.Equal(since, status.GetProperty("untilFull").GetProperty("since").GetDateTimeOffset());
    }
}
