using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

public class BatteryLoanLedgerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    // The poll cadence, which every step here has to stay inside: a longer silence is a gap the ledger
    // refuses to integrate across, and that is the subject of its own test below.
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Holds the two powers for <paramref name="minutes"/> of polling and returns where the clock got to,
    /// so a test reads as "2kW of lending for half an hour" rather than as a list of instants.
    /// </summary>
    private static DateTimeOffset Hold(
        BatteryLoanLedger ledger,
        DateTimeOffset from,
        int minutes,
        double loanPowerWatts,
        double batteryPowerWatts)
    {
        var at = from;
        for (var elapsed = 0; elapsed < minutes; elapsed += (int)Poll.TotalMinutes)
        {
            ledger.Add(at, loanPowerWatts, batteryPowerWatts);
            at = at.Add(Poll);
        }

        return at;
    }

    [Fact]
    public void LendingAccumulatesInBothTotals()
    {
        var ledger = new BatteryLoanLedger();

        // 2kW lent for half an hour, the pack discharging to fund it.
        var at = Hold(ledger, Noon, minutes: 30, loanPowerWatts: 2000, batteryPowerWatts: -2000);
        ledger.Add(at, 0, 0);

        Assert.Equal(1000, ledger.LentWattHours, 1);
        Assert.Equal(1000, ledger.OutstandingWattHours, 1);
    }

    [Fact]
    public void ChargingThePackBackClearsTheOutstandingHalfAndLeavesTheLentHalfAlone()
    {
        var ledger = new BatteryLoanLedger();

        var at = Hold(ledger, Noon, minutes: 30, loanPowerWatts: 2000, batteryPowerWatts: -2000);
        at = Hold(ledger, at, minutes: 60, loanPowerWatts: 0, batteryPowerWatts: 1500);
        ledger.Add(at, 0, 0);

        // Lent is the day's history and does not move; outstanding is what the pack is still down, and an
        // hour at 1.5kW has more than made the 1kWh loan good.
        Assert.Equal(1000, ledger.LentWattHours, 1);
        Assert.Equal(0, ledger.OutstandingWattHours, 1);
    }

    [Fact]
    public void RecoveryEarnsNoCreditAgainstLendingThatHasNotHappened()
    {
        var ledger = new BatteryLoanLedger();

        // A sunny morning charging the pack: the pack doing its own job, not a prepayment against a loan
        // nobody has asked for. Clamped per cycle, so it cannot run into credit.
        var at = Hold(ledger, Noon, minutes: 60, loanPowerWatts: 0, batteryPowerWatts: 3000);
        at = Hold(ledger, at, minutes: 30, loanPowerWatts: 2000, batteryPowerWatts: -2000);
        ledger.Add(at, 0, 0);

        Assert.Equal(1000, ledger.OutstandingWattHours, 1);
    }

    [Fact]
    public void ASilenceLongerThanTheGapIsNotIntegratedAcross()
    {
        var ledger = new BatteryLoanLedger(TimeSpan.FromMinutes(5));

        ledger.Add(Noon, loanPowerWatts: 2000, batteryPowerWatts: -2000);
        ledger.Add(Noon.AddHours(4), loanPowerWatts: 0, batteryPowerWatts: 0);

        // The service was not running; nothing was lent and nothing recovered that anybody observed.
        Assert.Equal(0, ledger.LentWattHours, 1);
        Assert.Equal(0, ledger.OutstandingWattHours, 1);
    }

    [Fact]
    public void ResetClearsBothTotals()
    {
        var ledger = new BatteryLoanLedger();

        Hold(ledger, Noon, minutes: 30, loanPowerWatts: 2000, batteryPowerWatts: -2000);
        ledger.Reset();

        Assert.Equal(0, ledger.LentWattHours, 1);
        Assert.Equal(0, ledger.OutstandingWattHours, 1);
    }
}
