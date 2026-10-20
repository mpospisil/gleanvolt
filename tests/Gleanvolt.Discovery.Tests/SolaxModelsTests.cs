using System.Text.RegularExpressions;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Discovery.Tests;

public class SolaxModelsTests
{
    [Theory]
    [InlineData("10K0000000000", "X3-HYB-G4 PRO", SupportTier.Verified)]
    [InlineData("H340000000000", "X3-Hybrid G4", SupportTier.Expected)]
    [InlineData("H3B0000000000", "X3-Ultra", SupportTier.Expected)]
    [InlineData("H3E0000000000", "X3-Hybrid G3", SupportTier.ReadingsAndChargingOnly)]
    [InlineData("H3DE000000000", "X3-Hybrid G3", SupportTier.ReadingsAndChargingOnly)]
    [InlineData("H450000000000", "X1-Hybrid G4", SupportTier.Expected)]
    [InlineData("F450000000000", "X1-RetroFit G4", SupportTier.NotSupported)]
    public void An_inverter_serial_names_its_family_and_tier(string serial, string model, SupportTier tier)
    {
        var found = SolaxModels.Inverter(serial);

        Assert.NotNull(found);
        Assert.Equal(PvDeviceRole.Inverter, found.Role);
        Assert.Equal(model, found.Model);
        Assert.Equal(tier, found.Tier);
    }

    [Theory]
    [InlineData("C3110000000000", "X3-EVC 11 kW", SupportTier.Expected)]
    [InlineData("5030B000000000", "X3-HAC 11 kW", SupportTier.Verified)]
    [InlineData("5020M000000000", "X1-HAC 22 kW", SupportTier.Verified)]
    [InlineData("50707000000000", "X3-HAC-S 7.2 kW", SupportTier.Expected)]
    public void A_charger_serial_names_its_model_and_tier(string serial, string model, SupportTier tier)
    {
        var found = SolaxModels.Charger(serial);

        Assert.NotNull(found);
        Assert.Equal(PvDeviceRole.Charger, found.Role);
        Assert.Equal(model, found.Model);
        Assert.Equal(tier, found.Tier);
    }

    [Theory]
    [InlineData("ZZZ0000000000")]
    [InlineData("5990B00000000")] // a HAC-shaped serial with a model code nobody lists
    [InlineData(null)]
    public void An_unknown_serial_names_nothing(string? serial)
    {
        Assert.Null(SolaxModels.Inverter(serial));
        Assert.Null(SolaxModels.Charger(serial));
    }

    [Fact]
    public void A_serial_is_two_characters_a_register_high_byte_first_and_padded_with_nul()
    {
        ushort[] registers = [0x3130, 0x4B41, 0x4243, 0x0000, 0x0000, 0x0000, 0x0000]; // "10KABC"

        Assert.Equal("10KABC", SolaxModels.DecodeSerialNumber(registers));
    }

    [Theory]
    [InlineData(new ushort[] { 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000 })]
    [InlineData(new ushort[] { 0x00FF, 0x1234, 0xFFFF, 0x0001, 0x0000, 0x0000, 0x0000 })]
    [InlineData(new ushort[] { 0x3130, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000 })] // "10": too short
    public void Registers_that_hold_no_serial_number_decode_to_none(ushort[] registers)
    {
        Assert.Null(SolaxModels.DecodeSerialNumber(registers));
    }

    /// <summary>
    /// The prefix table is in code once and in <c>SUPPORTED_PV_SYSTEMS.md</c> once, by hand. A prefix
    /// the tool names a device by and the doc does not list is a device the owner cannot look up.
    /// </summary>
    [Fact]
    public void Every_prefix_the_scan_names_a_device_by_is_listed_in_the_supported_systems_doc()
    {
        var doc = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "SUPPORTED_PV_SYSTEMS.md"));
        var listed = Regex.Matches(doc, "`([0-9A-Z]+)…?`").Select(match => match.Groups[1].Value).ToHashSet();

        foreach (var prefix in SolaxModels.InverterPrefixes.Concat(SolaxModels.ChargerPrefixes))
        {
            Assert.True(listed.Contains(prefix), $"{prefix} is in SolaxModels but not in docs/SUPPORTED_PV_SYSTEMS.md.");
        }
    }

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
