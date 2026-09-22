using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Infrastructure.Vehicles.Skoda;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The MyŠkoda Public API as the spec and its documentation describe it (issue #193), for the sign-in
/// and feed tests: spec-built bodies from <c>Fixtures/Skoda</c>, the headers the docs' prose names, and
/// a record of every request so "nothing was sent" is asserted rather than assumed.
/// </summary>
internal static class SkodaFixtures
{
    public const string BaseUrl = "https://skoda.test";
    public const string Vin = "TMBJB9NY5RF999999";
    public const string Key = "sk-live-0123456789abcdef";

    public static string Read(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Skoda", name));

    public static SkodaApiOptions Options() => new()
    {
        Enabled = true,
        Vin = Vin,
        BaseUrl = BaseUrl,
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>A secret store of the test's own, in a directory removed with the test.</summary>
    public static FileSecretStore SecretStore() => new(
        Path.Combine(Path.GetTempPath(), "gleanvolt-skoda-tests", Guid.NewGuid().ToString("N")));

    public static HttpResponseMessage Json(
        HttpStatusCode status,
        string fixture,
        DateTimeOffset? expiresAt = null,
        int? remaining = 17,
        int reset = 1800,
        TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(
                Read(fixture), Encoding.UTF8,
                (int)status < 300 ? "application/json" : "application/problem+json"),
        };

        if (expiresAt is { } expires)
        {
            response.Headers.TryAddWithoutValidation("X-API-Key-Expires-At", expires.ToString("O"));
        }

        if (remaining is { } left)
        {
            response.Headers.TryAddWithoutValidation("RateLimit-Limit", "20");
            response.Headers.TryAddWithoutValidation("RateLimit-Remaining", left.ToString());
            response.Headers.TryAddWithoutValidation("RateLimit-Reset", reset.ToString());
        }

        if (retryAfter is { } after)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(after);
        }

        return response;
    }

    /// <summary>A clock the test moves by hand.</summary>
    public sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The API: answers from a script and remembers what it was asked, key included.</summary>
    public sealed class FakeApi(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string? Key)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = answer;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-API-Key", out var keys) ? keys.Single() : null));

            if (request.Method != HttpMethod.Get)
            {
                throw new InvalidOperationException($"The client sent a {request.Method}; it must only read.");
            }

            return Task.FromResult(Answer(request));
        }
    }

    /// <summary>Keeps every formatted message, so "the key never reaches a log" can be checked.</summary>
    public sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
