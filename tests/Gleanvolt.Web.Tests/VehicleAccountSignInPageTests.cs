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

    private IRenderedComponent<VehiclePortal> Render(IVehicleAccountSignIn account)
    {
        Services.AddSingleton<IVehiclePortalReader>(new QuietReader());
        Services.AddSingleton(Car());
        Services.AddSingleton<IVehicleAccountSignIn>(account);

        // The page shows the car in the dashboard card's figures and against what the feed holds
        // (#178), so it needs a clock and a telemetry source even in tests that are only about the
        // sign-in box above them. Empty and stopped: nothing here reads either.
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddSingleton<IVehicleTelemetry>(new VehicleStateHolder());
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
    public void The_explanation_is_the_sign_ins_own()
    {
        var page = Render(new FakeVehicleAccountSignIn());

        Assert.Contains("the code Volkswagen emails", page.Markup);
    }

    /// <summary>A Škoda installation (#193): a key box straight away, and no word of Volkswagen.</summary>
    [Fact]
    public void A_key_sign_in_asks_for_a_key_in_a_password_box_without_being_pressed()
    {
        var account = new FakeKeySignIn();

        var page = Render(account);

        var box = page.Find("#account-key");
        Assert.Equal("password", box.GetAttribute("type"));
        Assert.Equal("off", box.GetAttribute("autocomplete"));
        Assert.Empty(page.FindAll("#account-code"));
        Assert.Empty(page.FindAll("#account-signin"));
        Assert.DoesNotContain("Volkswagen", page.Markup);
        Assert.Equal(0, account.Submissions);
    }

    [Fact]
    public void A_submitted_key_is_signed_in_and_never_rendered_back()
    {
        const string key = "sk-live-0123456789abcdef";
        var account = new FakeKeySignIn();

        var page = Render(account);
        page.Find("#account-key").Input(key);
        page.Find("#account-key-submit").Click();

        Assert.Equal(key, account.LastAnswer);
        Assert.Contains("Key valid until 2027-03-01", page.Markup);
        Assert.Single(page.FindAll("#account-signout"));
        Assert.DoesNotContain(key, page.Markup);
    }

    [Fact]
    public void A_refused_key_keeps_the_box_open_and_empty()
    {
        const string key = "sk-live-expired";
        var account = new FakeKeySignIn(
            VehicleSignInState.KeyRequired("That key has expired — create a new one in the app."));

        var page = Render(account);
        page.Find("#account-key").Input(key);
        page.Find("#account-key-submit").Click();

        Assert.Contains("That key has expired", page.Markup);
        Assert.Single(page.FindAll("#account-key"));
        Assert.DoesNotContain(key, page.Markup);
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

/// <summary>A sign-in that wants an API key, as the Škoda one does (issue #193).</summary>
internal sealed class FakeKeySignIn(VehicleSignInState? afterKey = null) : IVehicleAccountSignIn
{
    public int Submissions { get; private set; }

    public string? LastAnswer { get; private set; }

    public string AccountName => "MyŠkoda API";

    public string Explanation => "Create an API key in the MySkoda app and paste it here once.";

    public bool IsConfigured => true;

    public VehicleSignInState State { get; private set; } =
        VehicleSignInState.KeyRequired("Create an API key for this car in the MySkoda app and paste it here.");

    public Task<VehicleSignInState> SignInAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State);

    public Task<VehicleSignInState> SubmitAsync(string answer, CancellationToken cancellationToken = default)
    {
        Submissions++;
        LastAnswer = answer;
        State = afterKey ?? VehicleSignInState.SignedIn("Key valid until 2027-03-01 · My Enyaq");
        return Task.FromResult(State);
    }

    public void SignOut() =>
        State = VehicleSignInState.KeyRequired("Signed out; the key works until revoked in the app.");
}
