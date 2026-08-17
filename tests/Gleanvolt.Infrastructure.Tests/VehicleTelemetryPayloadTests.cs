using Gleanvolt.Core.Enums;
using Gleanvolt.Infrastructure.Vehicles;

namespace Gleanvolt.Infrastructure.Tests;

public class VehicleTelemetryPayloadTests
{
    private const string Full = """
        {"captured_at":"2026-08-17T10:44:23+00:00","soc_percent":28,
         "charge_state":"charging","plug_state":"connected","source":"id4"}
        """;

    [Fact]
    public void TryParse_ReadsEveryField()
    {
        Assert.True(VehicleTelemetryPayload.TryParse(Full, out var state, out var error));
        Assert.Null(error);

        Assert.Equal(DateTimeOffset.Parse("2026-08-17T10:44:23+00:00"), state!.CapturedAt);
        Assert.Equal(28, state.SocPercent);
        Assert.Equal(VehicleChargeState.Charging, state.ChargeState);
        Assert.Equal(VehiclePlugState.Connected, state.PlugState);
        Assert.Equal("id4", state.SourceId);
    }

    [Fact]
    public void TryParse_KeepsTheReportedOffsetRatherThanAssumingUtc()
    {
        // A source publishing local time must not be silently reinterpreted -- that would mis-age every
        // reading by the offset, and staleness is the whole reason CapturedAt is mandatory.
        Assert.True(VehicleTelemetryPayload.TryParse(
            """{"captured_at":"2026-08-17T12:44:23+02:00"}""", out var state, out _));

        Assert.Equal(TimeSpan.FromHours(2), state!.CapturedAt.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-08-17T10:44:23Z"), state.CapturedAt.ToUniversalTime());
    }

    [Theory]
    [InlineData("""{"captured_at":"2026-08-17T10:44:23+00:00"}""")]                       // SOC-less source
    [InlineData("""{"captured_at":"2026-08-17T10:44:23+00:00","soc_percent":null}""")]     // explicit null
    public void TryParse_AcceptsAnAbsentSoc(string payload)
    {
        // A source that simply doesn't report SOC is supported: OBD-only and settings-only feeds exist.
        Assert.True(VehicleTelemetryPayload.TryParse(payload, out var state, out _));
        Assert.Null(state!.SocPercent);
        Assert.Equal(VehicleChargeState.Unknown, state.ChargeState);
        Assert.Equal(VehiclePlugState.Unknown, state.PlugState);
    }

    [Theory]
    [InlineData(""""{"captured_at":"2026-08-17T10:44:23+00:00","soc_percent":"unavailable"}"""")]
    [InlineData("""{"captured_at":"2026-08-17T10:44:23+00:00","soc_percent":-1}""")]
    [InlineData("""{"captured_at":"2026-08-17T10:44:23+00:00","soc_percent":101}""")]
    public void TryParse_RejectsAnSocThatIsPresentButUnusable(string payload)
    {
        // "unavailable" is exactly what a Home Assistant template emits when its source entity drops
        // out, and half-trusting that payload is worse than dropping it: the holder then keeps the last
        // good reading and its age visibly grows, which is diagnosable.
        Assert.False(VehicleTelemetryPayload.TryParse(payload, out var state, out var error));
        Assert.Null(state);
        Assert.Contains("soc_percent", error);
    }

    [Theory]
    [InlineData("""{"soc_percent":28}""")]
    [InlineData("""{"captured_at":"unknown","soc_percent":28}""")]
    [InlineData("""{"captured_at":1755425063,"soc_percent":28}""")]
    public void TryParse_RejectsAPayloadWithoutAUsableCaptureTime(string payload)
    {
        // Including the epoch-number case: guessing between a unix timestamp and a mistake would
        // mis-age readings silently, so it is refused rather than interpreted.
        Assert.False(VehicleTelemetryPayload.TryParse(payload, out var state, out var error));
        Assert.Null(state);
        Assert.Contains("captured_at", error);
    }

    [Fact]
    public void TryParse_MapsAnUnrecognisedEnumToUnknownWithoutLosingTheSoc()
    {
        // Open-ended vocabularies: a car reporting a state we have never seen must not cost us its SOC.
        Assert.True(VehicleTelemetryPayload.TryParse(
            """
            {"captured_at":"2026-08-17T10:44:23+00:00","soc_percent":41,
             "charge_state":"CHARGE_STATE_CONSERVATION","plug_state":"who knows"}
            """,
            out var state,
            out var error));

        Assert.Null(error);
        Assert.Equal(41, state!.SocPercent);
        Assert.Equal(VehicleChargeState.Unknown, state.ChargeState);
        Assert.Equal(VehiclePlugState.Unknown, state.PlugState);
    }

    [Theory]
    [InlineData("charging", VehicleChargeState.Charging)]
    [InlineData("Charging", VehicleChargeState.Charging)]
    [InlineData("IDLE", VehicleChargeState.Idle)]
    [InlineData("complete", VehicleChargeState.Complete)]
    public void TryParse_MatchesEnumNamesCaseInsensitively(string reported, VehicleChargeState expected)
    {
        Assert.True(VehicleTelemetryPayload.TryParse(
            $$"""{"captured_at":"2026-08-17T10:44:23+00:00","charge_state":"{{reported}}"}""",
            out var state,
            out _));

        Assert.Equal(expected, state!.ChargeState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void TryParse_RejectsWhatIsNotAJsonObject(string? payload)
    {
        Assert.False(VehicleTelemetryPayload.TryParse(payload, out var state, out var error));
        Assert.Null(state);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_IgnoresUnknownProperties()
    {
        // Forward compatibility: a publisher adding a field must not break an older controller.
        Assert.True(VehicleTelemetryPayload.TryParse(
            """
            {"captured_at":"2026-08-17T10:44:23+00:00","soc_percent":28,
             "target_soc_percent":80,"odometer_km":51713}
            """,
            out var state,
            out var error));

        Assert.Null(error);
        Assert.Equal(28, state!.SocPercent);
    }
}
