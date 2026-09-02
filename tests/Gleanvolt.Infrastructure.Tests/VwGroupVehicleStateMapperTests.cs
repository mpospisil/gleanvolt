using Gleanvolt.Core.Enums;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The actual work of issue #139: a download holds several snapshots, so "take the last row" is
/// wrong, and three rules decide what a field is worth — sentinel filtering, largest-wins for
/// monotonic fields, last occurrence otherwise.
///
/// <para>#73's rules carry over unrenegotiated and are pinned here: absent is fine,
/// present-but-unusable is not, and an unrecognised enum value is the single exception that maps to
/// <c>Unknown</c> rather than costing us the state of charge.</para>
/// </summary>
public class VwGroupVehicleStateMapperTests
{
    private static IReadOnlyList<VwGroupSnapshot> Snapshots(params string[] fixtures)
    {
        Assert.True(VwGroupReportBundle.TryRead(VwGroupFixtures.Bundle(fixtures), out var snapshots, out _));
        return snapshots;
    }

    [Fact]
    public void MapsTheDottedIdxLayout()
    {
        var result = VwGroupVehicleStateMapper.Map(Snapshots("meb-dotted.json"), "id4");
        var state = Assert.IsType<Core.Models.VehicleState>(result.State);

        Assert.Equal(61, state.SocPercent);
        Assert.Equal(VehicleChargeState.Charging, state.ChargeState);
        Assert.Equal(VehiclePlugState.Connected, state.PlugState);
        Assert.Equal(TimeSpan.FromMinutes(70), state.ChargeTimeRemaining);
        Assert.Equal("id4", state.SourceId);
    }

    [Fact]
    public void MapsTheFlatPhevLayoutOntoTheSameShape()
    {
        // One vocabulary serves both, because a candidate is matched on the last dotted segment as
        // well as on the whole name. Two tables for two layouts would be two things to keep in step.
        var state = VwGroupVehicleStateMapper.Map(Snapshots("phev-flat.json")).State;

        Assert.NotNull(state);
        Assert.Equal(37, state.SocPercent);
        Assert.Equal(41, state.RangeKm);
        Assert.Equal(VehicleChargeState.Idle, state.ChargeState);
        Assert.Equal(VehiclePlugState.Disconnected, state.PlugState);
    }

    [Fact]
    public void ConvertsARangeTheFieldNameSaysIsInMetres()
    {
        // The portal states the unit in the field name, which is the only reason this can be
        // converted rather than guessed at from magnitude.
        Assert.Equal(348, VwGroupVehicleStateMapper.Map(Snapshots("meb-dotted.json")).State!.RangeKm);
    }

    [Fact]
    public void DatesTheReadingByTheNewestSnapshotThatContributed()
    {
        // Never the download time, and the offset survives.
        var state = VwGroupVehicleStateMapper.Map(Snapshots("meb-dotted.json")).State!;

        Assert.Equal(new DateTimeOffset(2026, 8, 31, 21, 29, 11, TimeSpan.FromHours(2)), state.CapturedAt);
    }

    [Fact]
    public void ABlankInTheNewestSnapshotDoesNotBeatARealReadingBehindIt()
    {
        // Sentinel filtering, the first tie-break and the one that matters most: without it the
        // newest snapshot wins by being newest even when it says nothing at all.
        var state = VwGroupVehicleStateMapper.Map(Snapshots("sentinels-and-partials.json")).State!;

        Assert.Equal(44, state.SocPercent);
    }

    [Fact]
    public void AMonotonicFieldTakesTheLargestValueRatherThanTheLast()
    {
        // An odometer cannot go backwards, so a smaller later value is a partial snapshot rather than
        // news -- and the last one in this bundle is a sentinel anyway.
        var result = VwGroupVehicleStateMapper.Map(Snapshots("sentinels-and-partials.json"));

        Assert.Equal(18240, result.OdometerKm);
    }

    [Fact]
    public void TheDefaultIsTheLastOccurrence()
    {
        // Two snapshots, both populated: the newer SOC is the reading. This is the rule everything
        // that genuinely moves both ways needs.
        Assert.Equal(61, VwGroupVehicleStateMapper.Map(Snapshots("meb-dotted.json")).State!.SocPercent);
    }

    [Fact]
    public void AMissingSocIsAReadingWithoutOneRatherThanAFailure()
    {
        // Absent is a supported configuration: no two sources report the same set, and a car that
        // reports a plug state and nothing else is still worth having.
        var snapshots = new[]
        {
            new VwGroupSnapshot(
                DateTimeOffset.Parse("2026-08-31T21:00:00+02:00"),
                new Dictionary<string, string> { ["charging.plugConnectionState"] = "PLUG_CONNECTION_STATE_CONNECTED" },
                "test"),
        };

        var state = VwGroupVehicleStateMapper.Map(snapshots).State;

        Assert.NotNull(state);
        Assert.Null(state.SocPercent);
        Assert.Equal(VehiclePlugState.Connected, state.PlugState);
    }

    [Fact]
    public void AnUnrecognisedChargeStateCostsTheStateAndNothingElse()
    {
        // The single exception to "present-but-unusable is a rejection". These vocabularies are
        // open-ended by nature, and an unfamiliar word must not cost us the SOC.
        var result = VwGroupVehicleStateMapper.Map(Snapshots("sentinels-and-partials.json"));

        Assert.NotNull(result.State);
        Assert.Equal(VehicleChargeState.Unknown, result.State.ChargeState);
        Assert.Equal(44, result.State.SocPercent);
    }

    [Fact]
    public void APresentButUnusableValueRejectsTheWholeBundle()
    {
        // #73's rule: the holder then keeps its last good reading and its age visibly grows, which is
        // a diagnosable state. Half-trusting junk is not.
        var result = VwGroupVehicleStateMapper.Map(Snapshots("unusable-soc.json"));

        Assert.Null(result.State);
        Assert.Contains("state of charge", result.Error);
        Assert.Contains("error", result.Error);
    }

    [Fact]
    public void AnOutOfRangeValueIsUnusableToo()
    {
        var snapshots = new[]
        {
            new VwGroupSnapshot(
                DateTimeOffset.Parse("2026-08-31T21:00:00+02:00"),
                new Dictionary<string, string> { ["battery.stateOfChargeInPercent"] = "127" },
                "test"),
        };

        Assert.Contains("outside 0-100", VwGroupVehicleStateMapper.Map(snapshots).Error);
    }

    [Fact]
    public void ReportsTheFieldsNothingHereReads()
    {
        // The portal's vocabulary was written down from a description rather than a capture, so the
        // first real download has to be able to say what is missing. Silence would cost a week of
        // wondering why the SOC is null.
        var result = VwGroupVehicleStateMapper.Map(Snapshots("meb-dotted.json"));

        Assert.Contains("climate.outsideTemperatureInCelsius", result.UnmappedFields);

        // And nothing it does read, including the two it reads but deliberately does not publish.
        Assert.DoesNotContain("battery.stateOfChargeInPercent", result.UnmappedFields);
        Assert.DoesNotContain("settings.target_soc", result.UnmappedFields);
        Assert.DoesNotContain("vehicle.mileageInKm", result.UnmappedFields);
    }

    [Fact]
    public void CarriesTheCarsOwnTargetWithoutActingOnIt()
    {
        // #101's impossible-target gate stays deferred. This exists so that "does it actually arrive
        // for this car?" is answerable without a code change -- which is #137's whole point about
        // what the portal gives that the MQTT feed never could.
        Assert.Equal(80, VwGroupVehicleStateMapper.Map(Snapshots("meb-dotted.json")).TargetSocPercent);
    }

    [Fact]
    public void NoSnapshotsIsAFailureWithAReason()
    {
        Assert.Contains("no snapshots", VwGroupVehicleStateMapper.Map([]).Error);
    }
    [Fact]
    public void ABundleWhoseFieldsAreAllStrangeIsRejectedRatherThanCalledAReading()
    {
        // Observed live: the portal answered, the bundle parsed, the capture time was minutes old --
        // and every value was blank, because not one field name matched. Called a success, that is a
        // page of dashes under a fresh timestamp, and an update service writing an empty reading over
        // a good one and resetting its age. "Absent is fine" was about a source that does not report a
        // field, never about a bundle in which everything is absent at once.
        var snapshot = new VwGroupSnapshot(
            new DateTimeOffset(2026, 9, 2, 10, 29, 46, TimeSpan.Zero),
            new Dictionary<string, string>
            {
                ["car_captured_time"] = "2026-09-02T10:29:46Z",
                ["some_field_nobody_wrote_down"] = "42",
                ["another.one"] = "VALID",
            },
            "report.json");

        var result = VwGroupVehicleStateMapper.Map([snapshot], "id4");

        Assert.Null(result.State);
        Assert.NotNull(result.Error);
        Assert.Contains("recognises", result.Error);

        // And the names travel with the rejection, because they are the fix.
        Assert.Contains("some_field_nobody_wrote_down", result.UnmappedFields);
        Assert.Contains("another.one", result.UnmappedFields);
    }

    [Fact]
    public void OneRecognisedFieldIsStillAReading()
    {
        // The guard must not become "everything or nothing": an OBD-shaped source that reports only a
        // state of charge is a supported feed, and #73's "absent is fine" still holds for the rest.
        var snapshot = new VwGroupSnapshot(
            new DateTimeOffset(2026, 9, 2, 10, 29, 46, TimeSpan.Zero),
            new Dictionary<string, string>
            {
                ["car_captured_time"] = "2026-09-02T10:29:46Z",
                ["battery_level_HV.value"] = "57",
                ["some_field_nobody_wrote_down"] = "42",
            },
            "report.json");

        var result = VwGroupVehicleStateMapper.Map([snapshot], "id4");

        Assert.NotNull(result.State);
        Assert.Equal(57, result.State.SocPercent);
        Assert.Contains("some_field_nobody_wrote_down", result.UnmappedFields);
    }


    [Fact]
    public void TheFieldsItDidRecogniseAreReportedWithWhatTheySaid()
    {
        // The half that was missing the first time this was needed in anger. A recognised name that
        // came back empty is invisible in the unmapped list -- it is not unmapped -- so an empty
        // reading looked identical whether the bundle lacked the field or carried it blank. Those
        // want opposite fixes.
        var snapshot = new VwGroupSnapshot(
            new DateTimeOffset(2026, 9, 2, 10, 29, 46, TimeSpan.Zero),
            new Dictionary<string, string>
            {
                ["car_captured_time"] = "2026-09-02T10:29:46Z",
                ["charging_state_report.charging_state"] = "invalid",
                ["mileage.value"] = "24680",
                ["settings.auto_unlock_ac"] = "true",
            },
            "report.json");

        var result = VwGroupVehicleStateMapper.Map([snapshot], "id4");

        // Recognised by leaf, and its value shown raw: "invalid" is a sentinel, which is why nothing
        // came of it, and seeing the word is the difference between adding a name and accepting that
        // the car sent nothing.
        Assert.Equal("invalid", result.MatchedFields["charging_state_report.charging_state"]);
        Assert.Equal("24680", result.MatchedFields["mileage.value"]);

        // And the two lists stay disjoint: a field is either recognised or listed as unrecognised.
        Assert.Contains("settings.auto_unlock_ac", result.UnmappedFields);
        Assert.DoesNotContain("mileage.value", result.UnmappedFields);
    }


    [Fact]
    public void TheBatterysEnergyContentIsRecognisedSoItCanBeSeen()
    {
        // The reference ID.4's delivery carries no state of charge at all; these two are the only
        // battery figures in it. Recognised rather than read: whether a percentage should be divided
        // out of them is a decision about what SocPercent means, and it wants the numbers in front of
        // somebody first.
        var snapshot = new VwGroupSnapshot(
            new DateTimeOffset(2026, 9, 2, 10, 29, 46, TimeSpan.Zero),
            new Dictionary<string, string>
            {
                ["car_captured_time"] = "2026-09-02T10:29:46Z",
                ["energy_contents.current_energy_content.physical_value"] = "41.2",
                ["energy_contents.maximal_energy_content.physical_value"] = "77",
            },
            "report.json");

        var result = VwGroupVehicleStateMapper.Map([snapshot], "id4");

        Assert.Equal("41.2", result.MatchedFields["energy_contents.current_energy_content.physical_value"]);
        Assert.DoesNotContain("energy_contents.current_energy_content.physical_value", result.UnmappedFields);

        // Seen, and still not read: nothing here invents a state of charge out of them yet.
        Assert.Null(result.State);
    }


    [Fact]
    public void WhereASnapshotCarriesTwoSpellingsTheListedPreferenceDecides()
    {
        // The candidate lists are ordered on purpose -- battery_level_HV.value leads because a real
        // bundle carries it with its own .state beside it. That order used to decide nothing: within
        // one snapshot the winner was whichever name the portal serialised first, which is the
        // dictionary's business and not a preference at all.
        var snapshot = new VwGroupSnapshot(
            new DateTimeOffset(2026, 9, 2, 10, 29, 46, TimeSpan.Zero),
            new Dictionary<string, string>
            {
                ["car_captured_time"] = "2026-09-02T10:29:46Z",
                // Listed second in the vocabulary, and first here, which is the whole trap.
                ["battery_state_report.soc"] = "60",
                ["battery_level_HV.value"] = "69",
            },
            "report.json");

        Assert.Equal(69, VwGroupVehicleStateMapper.Map([snapshot], "id4").State!.SocPercent);
    }


}
