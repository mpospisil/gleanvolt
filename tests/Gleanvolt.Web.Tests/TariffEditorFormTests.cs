using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The tariff on Settings (issue #265). What it has to get right: each rule shows on the row as the
/// owner types, Save stays disabled while anything is an error, warnings are saved through, nothing is
/// written before Save, and the preview draws what the form holds.
/// </summary>
public class TariffEditorFormTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly FakeTariffEditor _editor = new();

    public TariffEditorFormTests()
    {
        Services.AddSingleton<ITariffEditor>(_editor);

        // A login, which is what makes the form editable at all.
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));

        // Noon on a Wednesday in Prague, so today is a weekday and tomorrow is too.
        Services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero), Prague));
    }

    private static readonly TariffTable Night = new(
        TariffProvider.Table,
        [new("00:00", "06:00"), new("22:00", "24:00")],
        [new("00:00", "08:00")],
        2.10m,
        4.80m);

    private IRenderedComponent<TariffEditorForm> RenderTable()
    {
        _editor.Stored = Night;
        return Render<TariffEditorForm>();
    }

    private static bool SaveDisabled(IRenderedComponent<TariffEditorForm> form) =>
        form.Find("#tariff-save").HasAttribute("disabled");

    [Fact]
    public void WithoutALoginItCanBeReadButNotChanged()
    {
        using var open = new BunitContext();
        open.Services.AddSingleton<ITariffEditor>(new FakeTariffEditor { Stored = Night });
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

        Assert.Contains("everything would be high tariff", form.Find("#tariff-table-warning").TextContent);

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
        _editor.Stored = Night with { Weekday = [], Weekend = [] };
        var form = Render<TariffEditorForm>();

        form.Find("#tariff-weekday-add").Click();
        form.Find("#tariff-weekday-0-start").Input("13:00");
        form.Find("#tariff-weekday-0-end").Input("15:00");
        form.Find("#tariff-weekday-add").Click();
        form.Find("#tariff-weekday-1-start").Input("0:00");
        form.Find("#tariff-weekday-1-end").Input("6:00");
        form.Find("#tariff-save").Click();

        Assert.Single(_editor.Saves);
        Assert.Equal([new TariffPeriod("00:00", "06:00"), new TariffPeriod("13:00", "15:00")], _editor.Stored.Weekday);
        Assert.Equal("00:00", form.Find("#tariff-weekday-0-start").GetAttribute("value"));
        Assert.Contains("applies now", form.Find("#tariff-message").TextContent);
    }

    [Fact]
    public void SwitchingToNoneSavesWithoutTheTable()
    {
        var form = RenderTable();

        form.Find("#tariff-provider-none").Change(true);
        form.Find("#tariff-save").Click();

        Assert.Equal(TariffProvider.None, Assert.Single(_editor.Saves).Provider);
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
}
