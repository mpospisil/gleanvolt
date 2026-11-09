namespace Gleanvolt.Devices;

/// <summary>
/// How a charger driver reached over HTTP (go-e, issue #352) tells a read from a write. Modbus has separate
/// function codes for the two; an HTTP API may not (go-e's <c>/api/set</c> is a GET), so the driver marks every
/// request that only reads, and the host's dry-run client passes only those (<c>ReadOnlyHttpHandler</c>). A
/// request that is not marked is a write, so a driver that forgets the mark is refused in dry-run, not let through.
/// </summary>
public static class ChargerHttp
{
    /// <summary>The request option that marks a read.</summary>
    public static HttpRequestOptionsKey<bool> ReadOption { get; } = new("Gleanvolt.ChargerRead");

    /// <summary>A GET of <paramref name="relativeUri"/> (relative to the charger's base address) that only reads.</summary>
    public static HttpRequestMessage Read(string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Options.Set(ReadOption, true);
        return request;
    }

    /// <summary>A GET of <paramref name="relativeUri"/> that changes something on the charger.</summary>
    public static HttpRequestMessage Write(string relativeUri) =>
        new(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));

    /// <summary>Whether <paramref name="request"/> was made by <see cref="Read"/>: a GET marked as one.</summary>
    public static bool IsRead(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Method == HttpMethod.Get && request.Options.TryGetValue(ReadOption, out var read) && read;
    }

    /// <summary>
    /// The request's path and query as a recording keys it: relative, without a leading slash
    /// (<c>api/status?filter=car</c>).
    /// </summary>
    public static string PathAndQuery(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new ArgumentException("The request has no URI.", nameof(request));
        var text = uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
        return text.TrimStart('/');
    }
}
