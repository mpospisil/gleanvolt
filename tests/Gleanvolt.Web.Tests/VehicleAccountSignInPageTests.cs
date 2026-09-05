using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Signing in to volkswagen.de from the vehicle page (issue #170).
///
/// <para>The contract that matters here is negative. A cold login emails the owner a one-time code,
/// so a page that signs in while rendering would replay a password on every refresh and send codes
/// nobody asked for. Nothing may happen until a button is pressed.</para>
/// </summary>
public class VehicleAccountSignInPageTests : PageTest
{
    private static EvInfo Car() => new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77, 0.9, 3, 6, 16, "gleanvolt/vehicle/id4/state");

    private sealed class QuietReader : IVehiclePortalReader
    {
        public string PortalName => "VW Group EU Data Act portal";

        public bool IsConfigured => false;

        public string DescribeWhatIsMissing() => "a brand";

        public Task<VehiclePortalReading> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new VehiclePortalReading(true));
    }

    private IRenderedComponent<VehiclePortal> Render(FakeVehicleAccountSignIn account)
    {
        Services.AddSingleton<IVehiclePortalReader>(new QuietReader());
        Services.AddSingleton(Car());
        Services.AddSingleton<IVehicleAccountSignIn>(account);
        return Render<VehiclePortal>();
    }

    /// <summary>The one that would email the owner a code on every page refresh.</summary>
    [Fact]
    public void Rendering_never_signs_in_by_itself()
    {
        var account = new FakeVehicleAccountSignIn();

        var page = Render(account);
        page.Render();

        Assert.Equal(0, account.SignIns);
        Assert.Equal(0, account.CodeSubmissions);
    }

    [Fact]
    public void An_unconfigured_account_offers_nothing()
    {
        var page = Render(new FakeVehicleAccountSignIn(configured: false));

        Assert.Empty(page.FindAll("#account-signin"));
        Assert.DoesNotContain("volkswagen.de", page.Markup);
    }

    [Fact]
    public void Signing_in_asks_once_and_then_wants_the_code()
    {
        var account = new FakeVehicleAccountSignIn();

        var page = Render(account);
        page.Find("#account-signin").Click();

        Assert.Equal(1, account.SignIns);
        Assert.Single(page.FindAll("#account-code"));
        Assert.Contains("emailed", page.Markup);
    }

    [Fact]
    public void The_code_is_submitted_and_the_page_says_it_is_signed_in()
    {
        var account = new FakeVehicleAccountSignIn();

        var page = Render(account);
        page.Find("#account-signin").Click();
        page.Find("#account-code").Input("806324");
        page.Find("#account-code-submit").Click();

        Assert.Equal("806324", account.LastCode);
        Assert.Contains("Signed in", page.Markup);
        Assert.Single(page.FindAll("#account-signout"));
    }

    /// <summary>
    /// A wrong code leaves the challenge open. Starting over would email a third code and invalidate
    /// the one the owner is still reading.
    /// </summary>
    [Fact]
    public void A_rejected_code_keeps_the_box_open()
    {
        var account = new FakeVehicleAccountSignIn(
            afterCode: VehicleSignInState.CodeRequired("That code was not accepted. Check the newest email"));

        var page = Render(account);
        page.Find("#account-signin").Click();
        page.Find("#account-code").Input("000000");
        page.Find("#account-code-submit").Click();

        Assert.Single(page.FindAll("#account-code"));
        Assert.Contains("not accepted", page.Markup);
    }

    [Fact]
    public void An_already_signed_in_account_offers_sign_out_rather_than_a_code_box()
    {
        var account = new FakeVehicleAccountSignIn(first: VehicleSignInState.SignedIn("session restored"));

        var page = Render(account);
        page.Find("#account-signin").Click();

        Assert.Empty(page.FindAll("#account-code"));
        Assert.Single(page.FindAll("#account-signout"));
    }
}
