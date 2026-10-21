using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The tariff on Settings (issue #265). What it has to get right: the choice lists the strategies this
/// build registers and each brings its own panel; every rule shows in place as the owner types; Save
/// stays disabled while anything is an error and warnings save through; nothing is written before Save;
/// switching strategy and back keeps the first one's settings; and the preview draws what the form holds.
/// </summary>
public class TariffEditorFormTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly TariffTable NightTable = new(
        [new("00:00", "06:00"), new("22:00", "24:00")],
        [new("00:00", "08:00")]);

    private FakeTariffEditor _editor = new();

    public TariffEditorFormTests()
    {
        Services.AddSingleton<ITariffEditor>(_ => _editor);
        Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.Table, typeof(TariffTablePanel)));

        // A login, which is what makes the form editable at all.
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));

        // Noon on a Wednesday in Prague, so today is a weekday and tomorrow is too.
        Services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero), Prague));
    }

    private IRenderedComponent<TariffEditorForm> RenderTable(TariffTable? table = null)
    {
        _editor.Stored = TestTariffs.Table(table ?? NightTable);
        return Render<TariffEditorForm>();
    }

    private static bool SaveDisabled(IRenderedComponent<TariffEditorForm> form) =>
        form.Find("#tariff-save").HasAttribute("disabled");

    [Fact]
    public void WithoutALoginItCanBeReadButNotChanged()
    {
        using var open = new BunitContext();
        open.Services.AddSingleton<ITariffEditor>(new FakeTariffEditor { Stored = TestTariffs.Table(NightTable) });
        open.Services.AddSingleton(new TariffPanelRegistration(TariffProviderIds.Table, typeof(TariffTablePanel)));
        open.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.UnixEpoch, Prague));
        open.Services.AddSingleton(Options.Create(new WebOptions()));
        open.Services.AddSingleton(new WebPasswordState(null));

        var form = open.Render<TariffEditorForm>();

        Assert.Equal("22:00", form.Find("#tariff-weekday-1-start").GetAttribute("value"));
        Assert.All(form.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.Empty(form.FindAll("button"));
        Assert.Contains("no login", form.Find("#tariff-readonly").TextContent);
    }

    [Fact]
    public void WithNoProviderOnlyTheChoiceIsShown()
    {
        var form = Render<TariffEditorForm>();

        Assert.True(form.Find("#tariff-provider-none").HasAttribute("checked"));
        Assert.Empty(form.FindAll("#tariff-weekday"));
        Assert.Empty(form.FindAll("#tariff-low-price"));
        Assert.True(SaveDisabled(form));
    }

    [Fact]
    public void TheChoiceListsEveryRegisteredStrategy()
    {
        _editor = new FakeTariffEditor(new PanelLessProvider());

        var form = Render<TariffEditorForm>();

        Assert.Equal(["None", "Table", "Test feed"], form.FindAll("#tariff-provider label").Select(label => label.TextContent.Trim()));
    }

    [Fact]
    public void AStrategyWithoutAPanelHasNothingToSetButStillHasPrices()
    {
        _editor = new FakeTariffEditor(new PanelLessProvider());
        var form = Render<TariffEditorForm>();

        form.Find("#tariff-provider-testfeed").Change(true);

        Assert.Contains("Test feed has nothing to set here.", form.Find("#tariff-no-panel").TextContent);
        Assert.NotNull(form.Find("#tariff-low-price"));

        form.Find("#tariff-save").Click();

        Assert.Equal("TestFeed", Assert.Single(_editor.Saves).Provider);
    }

    [Fact]
    public void ASavedStrategyThisBuildLacksIsSaidAndBlocksSaveUntilAnotherIsChosen()
    {
        _editor.Stored = TestTariffs.Table(NightTable) with { Provider = "Egd" };
        var form = Render<TariffEditorForm>();

        Assert.Contains("Egd, is not in this build", form.Find("#tariff-provider-unknown").TextContent);

        form.Find("#tariff-provider-table").Change(true);

        Assert.Equal("22:00", form.Find("#tariff-weekday-1-start").GetAttribute("value"));
        Assert.False(SaveDisabled(form));
    }

    [Fact]
    public void TheSavedTableIsShownWithTheCurrencyOnThePrices()
    {
        var form = RenderTable();

        Assert.Equal("22:00", form.Find("#tariff-weekday-1-start").GetAttribute("value"));
        Assert.Equal("24:00", form.Find("#tariff-weekday-1-end").GetAttribute("value"));
        Assert.Equal("2.10", form.Find("#tariff-low-price").GetAttribute("value"));
        Assert.Contains("CZK/kWh", form.Find("label[for=tariff-low-price]").TextContent);
        Assert.True(SaveDisabled(form));
    }

    [Fact]
    public void NotATimeShowsOnTheRowAndBlocksSave()
    {
        var form = RenderTable();

        form.Find("#tariff-weekday-0-end").Input("6:6");

        Assert.Contains("Not a time.", form.Find("#tariff-weekday").TextContent);
        Assert.True(SaveDisabled(form));
    }

    [Fact]
    public void APeriodOverMidnightSaysToSplitIt()
    {
        var form = RenderTable();

        form.Find("#tariff-weekend-0-start").Input("22:00");
        form.Find("#tariff-weekend-0-end").Input("06:00");

        Assert.Contains("Split a period over midnight into two rows.", form.Find("#tariff-weekend").TextContent);
        Assert.True(SaveDisabled(form));
    }

    [Fact]
    public void AnOverlapShowsOnBothRows()
    {
        var form = RenderTable();

        form.Find("#tariff-weekday-1-start").Input("05:00");
        form.Find("#tariff-weekday-1-end").Input("07:00");

        var text = form.Find("#tariff-weekday").TextContent;
        Assert.Contains("Overlaps 05:00–07:00.", text);
        Assert.Contains("Overlaps 00:00–06:00.", text);
        Assert.True(SaveDisabled(form));
    }

    [Fact]
    public void ANegativeOrUnreadablePriceBlocksSave()
    {
        var form = RenderTable();

        form.Find("#tariff-high-price").Input("-1");
        Assert.Contains("can't be negative", form.Find("#tariff-high-price").ParentElement!.TextContent);
        Assert.True(SaveDisabled(form));

        form.Find("#tariff-high-price").Input("cheap");
        Assert.Contains("Not a price.", form.Find("#tariff-high-price").ParentElement!.TextContent);
        Assert.True(SaveDisabled(form));
    }

    [Fact]
    public void ALowPriceAboveTheHighOneWarnsButSaves()
    {
        var form = RenderTable();

        form.Find("#tariff-low-price").Input("5,20");

        Assert.Contains("Low tariff costs more than high.", form.Find("#tariff-low-price").ParentElement!.TextContent);
        Assert.False(SaveDisabled(form));

        form.Find("#tariff-save").Click();

        Assert.Equal(5.20m, Assert.Single(_editor.Saves).LowPricePerKwh);
    }

    [Fact]
    public void NoPeriodsAtAllWarnsButSaves()
    {
        var form = RenderTable();

        form.Find("#tariff-weekend-0-remove").Click();
        form.Find("#tariff-weekday-1-remove").Click();
        form.Find("#tariff-weekday-0-remove").Click();

        Assert.Contains("everything would be high tariff", form.Find("#tariff-table-problem").TextContent);

        form.Find("#tariff-save").Click();

        Assert.Single(_editor.Saves);
    }

    [Fact]
    public void ANewRowStartsWhereTheLastEnded()
    {
        var form = RenderTable();

        form.Find("#tariff-weekend-add").Click();

        Assert.Equal("08:00", form.Find("#tariff-weekend-1-start").GetAttribute("value"));
        Assert.Equal(string.Empty, form.Find("#tariff-weekend-1-end").GetAttribute("value"));
    }

    [Fact]
    public void NothingIsWrittenUntilSaveAndCancelPutsTheSavedTableBack()
    {
        var form = RenderTable();

        form.Find("#tariff-weekday-0-end").Input("07:00");

        Assert.Empty(_editor.Saves);

        form.Find("#tariff-cancel").Click();

        Assert.Equal("06:00", form.Find("#tariff-weekday-0-end").GetAttribute("value"));
        Assert.True(SaveDisabled(form));
    }

    [Fact]
    public void SaveStoresTheRowsSortedAndSaysItApplies()
    {
        var form = RenderTable(TariffTable.Empty);

        form.Find("#tariff-weekday-add").Click();
        form.Find("#tariff-weekday-0-start").Input("13:00");
        form.Find("#tariff-weekday-0-end").Input("15:00");
        form.Find("#tariff-weekday-add").Click();
        form.Find("#tariff-weekday-1-start").Input("0:00");
        form.Find("#tariff-weekday-1-end").Input("6:00");
        form.Find("#tariff-save").Click();

        Assert.Single(_editor.Saves);
        Assert.Equal(
            [new TariffPeriod("00:00", "06:00"), new TariffPeriod("13:00", "15:00")],
            TestTariffs.TableOf(_editor.Stored).Weekday);
        Assert.Equal("00:00", form.Find("#tariff-weekday-0-start").GetAttribute("value"));
        Assert.Contains("applies now", form.Find("#tariff-message").TextContent);
    }

    [Fact]
    public void SwitchingToNoneAndBackKeepsWhatWasTyped()
    {
        var form = RenderTable();

        form.Find("#tariff-weekday-0-end").Input("07:00");
        form.Find("#tariff-provider-none").Change(true);

        Assert.Empty(form.FindAll("#tariff-weekday"));

        form.Find("#tariff-provider-table").Change(true);

        Assert.Equal("07:00", form.Find("#tariff-weekday-0-end").GetAttribute("value"));
    }

    [Fact]
    public void SwitchingToNoneSavesTheSelectionAndKeepsTheTable()
    {
        var form = RenderTable();

        form.Find("#tariff-provider-none").Change(true);
        form.Find("#tariff-save").Click();

        Assert.True(Assert.Single(_editor.Saves).Selects(TariffProviderIds.None));
        Assert.Equal(2, TestTariffs.TableOf(_editor.Stored).Weekday.Count);
    }

    [Fact]
    public void ThePreviewShadesWhatTheFormHolds()
    {
        var form = RenderTable();

        var today = form.FindAll(".tariff-bar")[0];
        Assert.Equal("Today: low tariff 00:00–06:00, 22:00–24:00", today.GetAttribute("aria-label"));
        Assert.Equal(2, today.QuerySelectorAll(".tariff-low").Length);
        Assert.Equal("left: 0%; width: 25%", today.QuerySelector(".tariff-low")!.GetAttribute("style"));

        // A typo, 16:00 for 06:00, shows up as a wrong-looking day before anything is saved.
        form.Find("#tariff-weekday-0-end").Input("16:00");

        Assert.Equal(
            "Today: low tariff 00:00–16:00, 22:00–24:00",
            form.FindAll(".tariff-bar")[0].GetAttribute("aria-label"));
    }

    // A strategy registered only here, with no panel: proof that the form needs no change for a new one.
    private sealed class PanelLessProvider : ITariffProvider
    {
        public string Id => "TestFeed";

        public string Name => "Test feed";

        public IReadOnlyList<TariffProblem> Check(JsonElement settings) => [];

        public TariffTimeline? Expand(TariffContext context) => null;
    }
}
