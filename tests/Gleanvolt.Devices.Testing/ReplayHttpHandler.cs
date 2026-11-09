using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Gleanvolt.Devices.Testing;

/// <summary>
/// Answers a charger driver's HTTP requests from an <see cref="HttpDump"/> (issue #352), as
/// <see cref="ReplayModbusClient"/> does from a register dump.
///
/// <para>A read (<see cref="ChargerHttp.IsRead"/>) is answered with the recorded body for its exact path and
/// query, or 404 when nothing was recorded for it, as a web server answers a path it does not have. Anything
/// else is a write: it is recorded, changes nothing that is read back, and is answered by
/// <see cref="AnswerWrite"/>, which by default accepts every query key with <c>true</c>, go-e's answer to an
/// accepted <c>/api/set</c>.</para>
/// </summary>
public sealed class ReplayHttpHandler : HttpMessageHandler
{
    private readonly HttpDump _dump;
    private readonly List<string> _reads = [];
    private readonly List<string> _writes = [];

    public ReplayHttpHandler(HttpDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        _dump = dump;
    }

    /// <summary>Every read, by path and query, oldest first.</summary>
    public IReadOnlyList<string> Reads => _reads;

    /// <summary>Every write, by path and query, oldest first.</summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>How a write is answered. The default accepts every key of the query.</summary>
    public Func<string, HttpResponseMessage> AnswerWrite { get; set; } = AcceptEveryKey;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ChargerHttp.PathAndQuery(request);

        if (!ChargerHttp.IsRead(request))
        {
            _writes.Add(path);
            return Task.FromResult(AnswerWrite(path));
        }

        _reads.Add(path);
        return Task.FromResult(_dump.Responses.TryGetValue(path, out var body)
            ? Json(body?.ToJsonString() ?? "null")
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not found") });
    }

    /// <summary>A 200 with <paramref name="json"/> as its body.</summary>
    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage AcceptEveryKey(string path)
    {
        var answer = new JsonObject();
        var query = path.Contains('?', StringComparison.Ordinal) ? path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            answer[pair.Split('=')[0]] = true;
        }

        return Json(answer.ToJsonString());
    }
}
