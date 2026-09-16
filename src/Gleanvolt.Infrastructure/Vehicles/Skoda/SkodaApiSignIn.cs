using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.Skoda;

/// <summary>
/// The MyŠkoda API key behind <see cref="IVehicleAccountSignIn"/> (issue #193).
///
/// <para><b>Nothing to sign in to, only a key to check.</b> There is no session and no code: the owner
/// creates a key in the MySkoda app, pastes it, and one <c>GET …?include=info</c> proves the key, the
/// VIN, and that one is bound to the other — for one request of the hour's quota. Only a key that
/// passes is stored; a refused one is forgotten with a sentence saying which refusal it was.</para>
///
/// <para>Opening the page never sends anything. <see cref="State"/> and <see cref="SignInAsync"/> read
/// what is stored; the network is touched by <see cref="SubmitAsync"/> alone.</para>
/// </summary>
public sealed class SkodaApiSignIn(
    SkodaApiOptions options,
    SkodaApiKeyStore store,
    SkodaApiClient client,
    ILogger<SkodaApiSignIn>? logger = null) : IVehicleAccountSignIn
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    // The last answer, when it was something other than what the store implies -- a refusal, or the
    // sign-out's reminder that the key still works at Škoda. Cleared by the next change to the store.
    private VehicleSignInState? _said;
    private int _saidAtVersion = -1;

    public string AccountName => "MyŠkoda API";

    public string Explanation =>
        "The live source for this Škoda, read between charges as well as during them. Škoda asks for "
        + "an API key rather than a password: you create it in the MySkoda app, bound to this car, and "
        + "paste it here once. It is checked with Škoda, kept on this controller only, and never shown "
        + "again.";

    public bool IsConfigured => options.IsConfigured;

    public VehicleSignInState State
    {
        get
        {
            if (!IsConfigured)
            {
                return VehicleSignInState.NotConfigured;
            }

            if (_said is { } said && _saidAtVersion == store.Version)
            {
                return said;
            }

            return store.Current is { } key ? SignedIn(key, saved: true) : AskForKey;
        }
    }

    private static VehicleSignInState AskForKey { get; } = VehicleSignInState.KeyRequired(
        "Create an API key for this car in the MySkoda app and paste it here.");

    /// <summary>No network: a stored key is already signed in, and without one the answer is a key.</summary>
    public Task<VehicleSignInState> SignInAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State);

    public async Task<VehicleSignInState> SubmitAsync(string answer, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return VehicleSignInState.NotConfigured;
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return Say(VehicleSignInState.KeyRequired("Paste the API key from the MySkoda app."));
        }

        var key = answer.Trim();
        var response = await client.GetVehicleAsync(key, "info", cancellationToken).ConfigureAwait(false);

        if (response.Outcome != SkodaApiOutcome.Ok)
        {
            _logger.LogWarning(
                "Škoda refused the pasted API key for VIN {Vin} ({Outcome}, {Detail}); nothing was saved.",
                options.Vin, response.Outcome, response.Detail);

            return Say(VehicleSignInState.KeyRequired(SkodaApiSentences.ForSignIn(response.Outcome, options.Vin)));
        }

        var stored = new SkodaApiKey(key, response.KeyExpiresAt, SkodaVehicleResponse.Name(response.Body));
        var saved = store.Save(stored);

        _logger.LogInformation(
            "Škoda accepted the API key for VIN {Vin}; it expires {ExpiresAt:u}.", options.Vin, stored.ExpiresAt);

        if (!saved)
        {
            _logger.LogWarning(
                "The Škoda API key is in use but could not be saved to {Path}; a restart will ask for it again.",
                store.Path);
        }

        return Say(SignedIn(stored, saved));
    }

    /// <summary>
    /// Deletes the stored key. Said plainly that it still works: the API has no revoke endpoint, so
    /// only the app can end it.
    /// </summary>
    public void SignOut()
    {
        if (!IsConfigured)
        {
            return;
        }

        store.Clear();

        Say(VehicleSignInState.KeyRequired(
            "Signed out: the key is deleted from this controller, but it keeps working at Škoda until you "
            + "revoke it in the MySkoda app. Paste a key here to sign in again."));

        _logger.LogInformation("The Škoda API key for VIN {Vin} was deleted from this controller.", options.Vin);
    }

    private VehicleSignInState Say(VehicleSignInState state)
    {
        _said = state;
        _saidAtVersion = store.Version;
        return state;
    }

    private VehicleSignInState SignedIn(SkodaApiKey key, bool saved)
    {
        var validity = key.ExpiresAt is { } expires
            ? $"Key valid until {expires.LocalDateTime:yyyy-MM-dd}"
            : "Key accepted";

        var car = string.IsNullOrWhiteSpace(key.VehicleName) ? string.Empty : $" · {key.VehicleName}";

        var persistence = saved
            ? string.Empty
            : $". It could not be saved to {store.Path}, so a restart will ask for it again.";

        return VehicleSignInState.SignedIn(validity + car + persistence);
    }
}

/// <summary>
/// The sentences for the MyŠkoda API's refusals, shared by the page and the feed so a key problem reads
/// the same wherever it is met.
/// </summary>
public static class SkodaApiSentences
{
    public const string NoKey = "Paste an API key on the Vehicle portal page.";

    public static string ForSignIn(SkodaApiOutcome outcome, string vin) => outcome switch
    {
        SkodaApiOutcome.KeyExpired => "That key has expired — create a new one in the app.",
        SkodaApiOutcome.KeyUnknown => "Škoda did not recognise that key.",
        SkodaApiOutcome.KeyNotAuthorized =>
            $"That key is not allowed for VIN {vin}; include this car when creating it.",
        SkodaApiOutcome.VehicleNotFound =>
            $"Škoda has no car with VIN {vin} — check Vehicle:Skoda:Vin.",
        _ => "Škoda did not answer; nothing was saved — try again later.",
    };

    /// <summary>The feed's version of a key problem: the same fact, and where to go to fix it.</summary>
    public static string ForFeed(SkodaApiOutcome outcome, string vin) => outcome switch
    {
        SkodaApiOutcome.KeyExpired =>
            "The Škoda API key has expired — create a new one in the app and paste it on the Vehicle portal page.",
        SkodaApiOutcome.KeyUnknown =>
            "Škoda no longer recognises the stored API key — paste a new one on the Vehicle portal page.",
        SkodaApiOutcome.KeyNotAuthorized =>
            $"The stored Škoda API key is not allowed for VIN {vin} — create one that includes this car and "
            + "paste it on the Vehicle portal page.",
        SkodaApiOutcome.VehicleNotFound =>
            $"Škoda has no car with VIN {vin} — check Vehicle:Skoda:Vin.",
        _ => ForSignIn(outcome, vin),
    };
}
