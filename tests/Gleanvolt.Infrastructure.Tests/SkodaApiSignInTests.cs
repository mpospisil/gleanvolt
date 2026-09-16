using System.Net;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Vehicles.Skoda;
using static Gleanvolt.Infrastructure.Tests.SkodaFixtures;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// Pasting a MyŠkoda API key on the Vehicle portal page (issue #193): one request proves it, only a
/// key that passes is kept, and each refusal is its own sentence.
/// </summary>
public sealed class SkodaApiSignInTests : IDisposable
{
    private static readonly DateTimeOffset Expiry = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _keyPath = KeyPath();
    private readonly FakeApi _api = new(_ => Json(HttpStatusCode.OK, "info.json", Expiry));
    private readonly CapturingLogger<SkodaApiSignIn> _log = new();
    private readonly SkodaApiKeyStore _store;
    private readonly SkodaApiClient _client;
    private readonly SkodaApiSignIn _signIn;

    public SkodaApiSignInTests()
    {
        _store = new SkodaApiKeyStore(_keyPath);
        _client = new SkodaApiClient(Options(), transport: _api);
        _signIn = new SkodaApiSignIn(Options(), _store, _client, _log);
    }

    public void Dispose()
    {
        _client.Dispose();
        var directory = Path.GetDirectoryName(_keyPath)!;

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task With_no_key_it_asks_for_one_and_sends_nothing()
    {
        Assert.True(_signIn.State.WantsKey);
        Assert.True((await _signIn.SignInAsync()).WantsKey);
        Assert.Empty(_api.Requests);
        Assert.DoesNotContain("Volkswagen", _signIn.Explanation);
    }

    [Fact]
    public async Task A_valid_key_is_checked_once_stored_and_signed_in_with_its_expiry_and_the_cars_name()
    {
        var state = await _signIn.SubmitAsync($"  {Key}  ");

        Assert.True(state.IsSignedIn);
        Assert.Contains("Key valid until 2027-03-01", state.Message);
        Assert.Contains("My Enyaq", state.Message);

        var request = Assert.Single(_api.Requests);
        Assert.Equal($"{BaseUrl}/api/v1/vehicles/{Vin}?include=info", request.Url);
        Assert.Equal(Key, request.Key);

        Assert.Equal(Key, _store.Current?.Key);
        Assert.True(_signIn.State.IsSignedIn);
    }

    [Fact]
    public async Task The_key_survives_a_restart_in_an_owner_only_file()
    {
        await _signIn.SubmitAsync(Key);

        var restarted = new SkodaApiKeyStore(_keyPath);
        Assert.Equal(Key, restarted.Current?.Key);
        Assert.Equal(Expiry, restarted.Current?.ExpiresAt);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_keyPath));
        }

        // Signed in after the restart without asking Škoda again.
        using var client = new SkodaApiClient(Options(), transport: _api);
        var again = new SkodaApiSignIn(Options(), restarted, client);
        Assert.True((await again.SignInAsync()).IsSignedIn);
        Assert.Single(_api.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "problem-api-key-expired.json", "That key has expired")]
    [InlineData(HttpStatusCode.Unauthorized, "problem-unauthorized.json", "did not recognise that key")]
    [InlineData(HttpStatusCode.Forbidden, "problem-api-key-not-authorized.json", $"not allowed for VIN {Vin}")]
    [InlineData(HttpStatusCode.NotFound, "problem-not-found.json", $"has no car with VIN {Vin}")]
    [InlineData(HttpStatusCode.TooManyRequests, "problem-rate-limit-exceeded.json", "did not answer; nothing was saved")]
    [InlineData(HttpStatusCode.InternalServerError, "problem-unauthorized.json", "did not answer; nothing was saved")]
    public async Task Each_refusal_has_its_own_sentence_and_stores_nothing(
        HttpStatusCode status, string fixture, string sentence)
    {
        _api.Answer = _ => Json(status, fixture);

        var state = await _signIn.SubmitAsync(Key);

        Assert.True(state.WantsKey);
        Assert.Contains(sentence, state.Message);
        Assert.Null(_store.Current);
        Assert.False(File.Exists(_keyPath));
    }

    [Fact]
    public async Task An_unreachable_api_stores_nothing()
    {
        _api.Answer = _ => throw new HttpRequestException("connection refused");

        var state = await _signIn.SubmitAsync(Key);

        Assert.Contains("did not answer; nothing was saved", state.Message);
        Assert.Null(_store.Current);
    }

    [Fact]
    public async Task Sign_out_deletes_the_key_and_says_it_still_works_until_revoked()
    {
        await _signIn.SubmitAsync(Key);

        _signIn.SignOut();

        Assert.Null(_store.Current);
        Assert.False(File.Exists(_keyPath));
        Assert.True(_signIn.State.WantsKey);
        Assert.Contains("revoke it in the MySkoda app", _signIn.State.Message);
    }

    /// <summary>Bearer-equivalent: not in a sentence for the page, and not in a log line.</summary>
    [Fact]
    public async Task The_key_never_reaches_a_message_or_a_log()
    {
        await _signIn.SubmitAsync(Key);
        var signedIn = _signIn.State.Message;

        _api.Answer = _ => Json(HttpStatusCode.Unauthorized, "problem-unauthorized.json");
        var refused = (await _signIn.SubmitAsync(Key + "x")).Message;

        Assert.DoesNotContain(Key, signedIn);
        Assert.DoesNotContain(Key, refused);
        Assert.DoesNotContain(Key, _store.Current!.ToString());
        Assert.NotEmpty(_log.Messages);
        Assert.All(_log.Messages, message => Assert.DoesNotContain(Key, message));
    }

    [Fact]
    public void Not_configured_offers_nothing()
    {
        using var client = new SkodaApiClient(new SkodaApiOptions(), transport: _api);
        var signIn = new SkodaApiSignIn(new SkodaApiOptions(), _store, client);

        Assert.False(signIn.IsConfigured);
        Assert.Equal(VehicleSignInStatus.NotConfigured, signIn.State.Status);
    }
}
