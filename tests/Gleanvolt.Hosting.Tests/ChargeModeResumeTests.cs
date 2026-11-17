using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Resume;

namespace Gleanvolt.Hosting.Tests;

/// <summary>Each mode's rule for being taken up again after a restart (#413).</summary>
public sealed class ChargeModeResumeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Within = TimeSpan.FromHours(12);

    private static ChargeModeResumeDecision? Decide(SavedChargeMode? saved) => ChargeModeResume.Decide(saved, Now, Within);

    [Fact]
    public void NothingSaved_OrOff_HasNothingToSay()
    {
        Assert.Null(Decide(null));
        Assert.Null(Decide(new SavedChargeMode(ChargeControlMode.Off, Now.AddMinutes(-1))));
    }

    [Theory]
    [InlineData(ChargeControlMode.Solar)]
    [InlineData(ChargeControlMode.Forecasted)]
    [InlineData(ChargeControlMode.SolarGrid)]
    [InlineData(ChargeControlMode.FastNoBattery)]
    public void AShortAbsence_Resumes(ChargeControlMode mode) =>
        Assert.True(Decide(new SavedChargeMode(mode, Now.AddMinutes(-3)))?.Resume);

    [Theory]
    [InlineData(ChargeControlMode.Solar)]
    [InlineData(ChargeControlMode.Forecasted)]
    [InlineData(ChargeControlMode.SolarGrid)]
    [InlineData(ChargeControlMode.FastNoBattery)]
    public void AnAbsenceLongerThanResumeWithin_StaysOff(ChargeControlMode mode)
    {
        var decision = Decide(new SavedChargeMode(mode, Now.AddHours(-13)));

        Assert.False(decision?.Resume);
        Assert.Contains("ResumeWithin", decision!.Reason);
    }

    [Fact]
    public void ResumeWithinZero_TurnsResumingOff() =>
        Assert.False(ChargeModeResume.Decide(
            new SavedChargeMode(ChargeControlMode.Solar, Now.AddMinutes(-1)), Now, TimeSpan.Zero)?.Resume);

    [Fact]
    public void Targeted_ResumesHoweverLongTheAbsence_WhileItsDepartureIsAhead()
    {
        var request = new TargetedChargeRequest(10_000, DepartBy: Now.AddHours(2), ActivatedAt: Now.AddDays(-2));

        Assert.True(Decide(new SavedChargeMode(ChargeControlMode.Targeted, Now.AddDays(-1), TargetedRequest: request))?.Resume);
    }

    [Fact]
    public void Targeted_WhoseDeparturePassed_StaysOff_AndSaysWhy()
    {
        var request = new TargetedChargeRequest(10_000, DepartBy: Now.AddMinutes(-1), ActivatedAt: Now.AddHours(-8));
        var decision = Decide(new SavedChargeMode(ChargeControlMode.Targeted, Now.AddMinutes(-5), TargetedRequest: request));

        Assert.False(decision?.Resume);
        Assert.Contains("departure", decision!.Reason);
    }

    [Fact]
    public void Targeted_AlreadyDelivered_OrWithNoTarget_StaysOff()
    {
        var request = new TargetedChargeRequest(10_000, DepartBy: Now.AddHours(2), ActivatedAt: Now.AddHours(-8));

        Assert.False(Decide(new SavedChargeMode(
            ChargeControlMode.Targeted, Now.AddMinutes(-1), TargetedRequest: request, TargetedDeliveredWh: 10_000))?.Resume);
        Assert.False(Decide(new SavedChargeMode(ChargeControlMode.Targeted, Now.AddMinutes(-1)))?.Resume);
    }

    [Fact]
    public void AScheduledFastCharge_ResumesHoweverLongTheAbsence_UntilItsDeparturePasses()
    {
        var ahead = new FastChargeLimit(20_000, ActivatedAt: Now.AddDays(-1), DepartBy: Now.AddHours(1));
        var passed = ahead with { DepartBy = Now.AddMinutes(-1) };

        Assert.True(Decide(new SavedChargeMode(ChargeControlMode.FastNoBattery, Now.AddHours(-20), FastLimit: ahead))?.Resume);
        Assert.False(Decide(new SavedChargeMode(ChargeControlMode.FastNoBattery, Now.AddMinutes(-5), FastLimit: passed))?.Resume);
    }

    [Fact]
    public void AFastLimitAlreadyMet_StaysOff() =>
        Assert.False(Decide(new SavedChargeMode(
            ChargeControlMode.FastNoBattery,
            Now.AddMinutes(-1),
            FastLimit: new FastChargeLimit(5_000, Now.AddHours(-1)),
            FastDeliveredWh: 5_000))?.Resume);

    [Fact]
    public void ForecastedUntilFull_ResumesHoweverLongTheAbsence() =>
        Assert.True(Decide(new SavedChargeMode(ChargeControlMode.Forecasted, Now.AddDays(-3), UntilFull: true))?.Resume);
}

/// <summary>The file the running mode is kept in (#413).</summary>
public sealed class ChargeModeFileTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"charge-mode-file-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_directory, "charge-mode.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ChargeModeFile Open() => new(FilePath, NullLogger<ChargeModeFile>.Instance);

    [Fact]
    public void ATargetRoundTrips_ConstraintsAndAll()
    {
        var request = new TargetedChargeRequest(
            12_000,
            DepartBy: Now.AddHours(9),
            ActivatedAt: Now,
            TargetSocPercent: 80,
            VehicleSocPercentAtRequest: 42,
            Priority: TargetedChargePriority.JustInTime,
            TailEnergyWh: 3_000,
            RestSocPercent: 60,
            Constraints: new TargetedChargeConstraints(
                ForbiddenWindows: [new TimeWindow(Now.AddHours(1), Now.AddHours(2))], LowTariffOnly: true));
        var saved = new SavedChargeMode(ChargeControlMode.Targeted, Now, TargetedRequest: request, TargetedDeliveredWh: 1_234);

        Open().Track(saved);
        var read = Open().Saved;

        Assert.NotNull(read);
        Assert.False(saved.DiffersFrom(read));
        Assert.Equal(1_234, read.TargetedDeliveredWh);
        Assert.Equal(Now, read.SavedAt);
    }

    [Fact]
    public void AFastLimitRoundTrips()
    {
        var saved = new SavedChargeMode(
            ChargeControlMode.FastNoBattery, Now, FastLimit: new FastChargeLimit(20_000, Now, 80, 40, Now.AddHours(10)), FastDeliveredWh: 500);

        Open().Track(saved);

        Assert.Equal(saved, Open().Saved);
    }

    [Fact]
    public void ARunningMode_IsRewrittenOnlyWhenItChanges_OrIsDueARefresh()
    {
        var file = Open();
        var saved = new SavedChargeMode(ChargeControlMode.FastNoBattery, Now, FastLimit: new FastChargeLimit(20_000, Now));

        file.Track(saved);
        file.Track(saved with { SavedAt = Now.AddMinutes(1), FastDeliveredWh = 180 });
        Assert.Equal(Now, Open().Saved?.SavedAt);

        file.Track(saved with { SavedAt = Now.AddMinutes(5), FastDeliveredWh = 900 });
        Assert.Equal(900, Open().Saved?.FastDeliveredWh);

        file.Track(saved with { SavedAt = Now.AddMinutes(6), Mode = ChargeControlMode.Off, FastLimit = null });
        Assert.Equal(ChargeControlMode.Off, Open().Saved?.Mode);
    }

    [Fact]
    public void Flush_SavesTheLastPoll_StampedWithTheStop()
    {
        var file = Open();
        var saved = new SavedChargeMode(ChargeControlMode.FastNoBattery, Now, FastLimit: new FastChargeLimit(20_000, Now));

        file.Track(saved);
        file.Track(saved with { SavedAt = Now.AddMinutes(2), FastDeliveredWh = 360 });
        file.Flush(Now.AddMinutes(3));

        var read = Open().Saved;
        Assert.Equal(Now.AddMinutes(3), read?.SavedAt);
        Assert.Equal(360, read?.FastDeliveredWh);
    }

    [Fact]
    public void Flush_WithNothingTracked_LeavesTheFileAlone()
    {
        Open().Track(new SavedChargeMode(ChargeControlMode.Solar, Now));

        Open().Flush(Now.AddHours(1));

        Assert.Equal(Now, Open().Saved?.SavedAt);
    }

    [Fact]
    public void AFileThatCannotBeRead_IsNothingSaved()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Null(Open().Saved);
    }
}
