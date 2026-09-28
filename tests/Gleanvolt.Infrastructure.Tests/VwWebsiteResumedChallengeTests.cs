using System.Net;
using System.Net.Sockets;
using System.Text;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Infrastructure.Vehicles.VwWebsite;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The loop an owner could not get out of, reproduced.
///
/// <para>volkswagen.de remembers a code challenge across our restarts, so the login page can ask for a
/// code without our having posted any credentials. <c>SignInAsync</c> reported that step and returned —
/// without recording where the answer had to be posted. The UI then showed a code box, every code came
/// back as "the sign-in did not complete", and neither end logged anything at all. The only escape was
/// a sign-out, which nothing told anyone to try.</para>
/// </summary>
public sealed class VwWebsiteResumedChallengeTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    // A real file store in a throwaway directory, as the sibling suite does: the resumed challenge is
    // saved through it, so a fake that forgot would hide the thing under test.
    private readonly FileSecretStore _secrets = new(
        Path.Combine(Path.GetTempPath(), $"gleanvolt-vw-resumed-{Guid.NewGuid():N}"));
    private readonly VwWebsiteOptions _options;
    private readonly List<string> _posts = [];

    public VwWebsiteResumedChallengeTests()
    {
        _listener.Start();
        _ = ServeAsync();

        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _options = new VwWebsiteOptions
        {
            Enabled = true,
            Username = "owner@example.com",
            Password = "hunter2",
            Vin = "WVGZZZE2ZPE999999",
            PortalBaseUrl = baseUrl,
            IdentityBaseUrl = baseUrl,
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private VwWebsiteClient NewClient() => new(_options, new VwWebsiteSessionStore(_secrets));

    // Every request is answered with a page that reads as "a one-time code is wanted", which is what
    // a provider holding an open challenge does. POST bodies are kept so a test can assert that the
    // code actually left the building.
    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var socket = await _listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var buffer = new byte[16384];
                var read = await stream.ReadAsync(buffer);
                var request = Encoding.ASCII.GetString(buffer, 0, read);

                if (request.StartsWith("POST", StringComparison.Ordinal))
                {
                    lock (_posts)
                    {
                        _posts.Add(request);
                    }
                }

                var body = "<form action=\"/u/mfa-email-challenge\">"
                    + "<input name=\"state\" value=\"resumed-state\"><input name=\"code\"></form>";
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n"
                    + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");

                await stream.WriteAsync(head);
                await stream.WriteAsync(bytes);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // The listener was disposed; the test is over.
        }
    }

    private int PostCount()
    {
        lock (_posts)
        {
            return _posts.Count;
        }
    }

    [Fact]
    public async Task A_challenge_the_provider_already_holds_is_answerable()
    {
        using var client = NewClient();

        var step = await client.SignInAsync();

        Assert.Equal(VwWebsiteLoginStep.OneTimeCodeRequired, step);

        // The regression: this was false, so the code box was a dead end.
        Assert.True(client.AwaitingCode, "the resumed challenge was not captured, so no code could be posted");

        var before = PostCount();
        await client.SubmitCodeAsync("123456");

        Assert.True(PostCount() > before, "the code was never posted anywhere");
    }

    [Fact]
    public async Task The_posted_code_carries_the_state_the_provider_asked_for()
    {
        using var client = NewClient();
        await client.SignInAsync();

        await client.SubmitCodeAsync("123456");

        string posted;
        lock (_posts)
        {
            posted = string.Join("\n", _posts);
        }

        Assert.Contains("code=123456", posted, StringComparison.Ordinal);
        Assert.Contains("resumed-state", posted, StringComparison.Ordinal);

        // The grant that stops the next restart asking again.
        Assert.Contains("rememberBrowser=true", posted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_with_no_challenge_open_is_not_sent_and_says_so()
    {
        using var client = NewClient();

        // No SignInAsync first: nothing is pending.
        var step = await client.SubmitCodeAsync("123456");

        Assert.Equal(VwWebsiteLoginStep.NoChallenge, step);
        Assert.Equal(0, PostCount());
    }

    [Fact]
    public async Task The_owner_is_told_it_was_local_rather_than_warned_about_a_lockout()
    {
        using var client = NewClient();
        var signIn = new VwWebsiteSignIn(_options, client);

        var state = await signIn.SubmitAsync("123456");

        // The old message sent an owner off to wait and worry about their Volkswagen account over
        // state that never left this process.
        Assert.DoesNotContain("locking the account", state.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sign in", state.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _listener.Dispose();
}
